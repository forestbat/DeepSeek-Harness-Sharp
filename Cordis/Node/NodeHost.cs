using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Cordis.Node;

public sealed class HandleTable
{
    private long _nextId = -1;
    private readonly ConcurrentDictionary<long, object> _byId = new();
    private readonly Dictionary<object, long> _byValue = new(ReferenceEqualityComparer.Instance);
    private readonly object _sync = new();

    public long Store(object value)
    {
        lock (_sync)
        {
            if (_byValue.TryGetValue(value, out var id)) return id;
            id = _nextId--;
            _byId[id] = value;
            _byValue[value] = id;
            return id;
        }
    }

    public object? Resolve(long id) => _byId.TryGetValue(id, out var value) ? value : null;
}

public sealed class PromiseTable(NodeHost host)
{
    private long _nextId = -1;
    private readonly ConcurrentDictionary<long, Task> _local = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<object?>> _remote = new();

    public JsPromise Track(object? result)
    {
        var task = result switch
        {
            Task<object?> t => t,
            ValueTask<object?> vt => vt.AsTask(),
            Task t => AwaitVoid(t),
            _ => Task.FromResult(result),
        };
        var id = _nextId--;
        _local[id] = task;
        _ = Observe(id, task);
        return new JsPromise(host, id);
    }

    private static async Task<object?> AwaitVoid(Task task)
    {
        await task;
        return null;
    }

    private async Task Observe(long id, Task task)
    {
        try
        {
            var value = task is Task<object?> typed ? await typed : await AwaitVoid(task);
            await host.NotifyAsync("pres", new JsonObject
            {
                ["id"] = id,
                ["ok"] = true,
                ["result"] = NodeMarshal.Marshal(host, value),
            });
        }
        catch (Exception error)
        {
            await host.NotifyAsync("pres", new JsonObject
            {
                ["id"] = id,
                ["ok"] = false,
                ["result"] = NodeMarshal.Marshal(host, error),
            });
        }
    }

    public Task<object?> ResolveLocal(long id) => (Task<object?>?)_local.GetValueOrDefault(id) ?? Task.FromResult<object?>(null);

    public Task<object?> TrackRemote(long id)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _remote[id] = tcs;
        return tcs.Task;
    }

    internal bool Settle(long id, bool ok, object? value)
    {
        if (!_remote.TryRemove(id, out var tcs)) return false;
        if (ok) tcs.TrySetResult(value);
        else tcs.TrySetException(value is Exception error ? error : new JsRemoteException($"{value}", null));
        return true;
    }
}

public sealed class NodeHost : IDisposable
{
    private readonly Process _process;
    private readonly RpcConnection _rpc;

    public HandleTable Handles { get; } = new();
    public PromiseTable Promises { get; }
    public Context? RootContext { get; set; }

    private readonly Dictionary<string, PluginDefinition> _plugins = new();

    public PluginDefinition GetPlugin(string key, string? name, bool hasConfig, object? inject = null)
    {
        lock (_plugins)
        {
            if (_plugins.TryGetValue(key, out var existing)) return existing;
            var definition = new PluginDefinition
            {
                Name = name,
                Inject = ParseInject(inject),
                Callback = new JsPluginCallback(this, key),
                ConfigValidator = hasConfig ? new JsConfigValidator(this, key) : null,
            };
            _plugins[key] = definition;
            return definition;
        }
    }

    internal static Dictionary<string, object?>? ParseInject(object? inject)
    {
        switch (inject)
        {
            case null or JsUndefined:
                return null;
            case IDictionary<string, object?> dict:
                return new Dictionary<string, object?>(dict);
            case IEnumerable<object?> list:
                var result = new Dictionary<string, object?>();
                foreach (var item in list)
                {
                    if (item is string dep) result[dep] = null;
                }
                return result;
            default:
                return null;
        }
    }

    public Action<string>? OnStdOut { get; set; }
    public Action<string>? OnStdErr { get; set; }

    private NodeHost(Process process, RpcConnection rpc)
    {
        _process = process;
        _rpc = rpc;
        Promises = new PromiseTable(this);
    }

    public static NodeHost Start(string? nodeExecutable = null)
    {
        var shimPath = ExtractShim();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodeExecutable ?? "node",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add(shimPath);
        process.Start();
        var rpc = new RpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        var host = new NodeHost(process, rpc);
        rpc.OnRequest = host.HandleRequest;
        rpc.OnNotification = host.HandleNotification;
        return host;
    }

    private static int _shimCounter;

    private static string ExtractShim()
    {
        var assembly = typeof(NodeHost).Assembly;
        const string resourceName = "Cordis.Node.Shim.cordis-shim.mjs";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new CordisException("SHIM_MISSING", $"embedded resource {resourceName} not found");
        var instance = Interlocked.Increment(ref _shimCounter);
        var path = Path.Combine(Path.GetTempPath(), "cordis-shim",
            $"cordis-shim-{typeof(NodeHost).Assembly.GetName().Version}-{Environment.ProcessId}-{instance}.mjs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        {
            stream.CopyTo(file);
        }
        return path;
    }

    public async Task<object?> RequestAsync(string method, JsonNode? @params, CancellationToken cancellationToken = default)
    {
        var result = await _rpc.RequestAsync(method, @params, cancellationToken);
        return NodeMarshal.Unmarshal(this, result);
    }

    public Task NotifyAsync(string method, JsonNode? @params) => _rpc.NotifyAsync(method, @params);

    public async Task<object?> InvokeCallbackAsync(long id, object? thisArg, object?[] args)
    {
        return await RequestAsync("cb", new JsonObject
        {
            ["id"] = id,
            ["thisArg"] = NodeMarshal.Marshal(this, thisArg),
            ["args"] = NodeMarshal.Marshal(this, args),
        });
    }

    private void HandleNotification(JsonNode message)
    {
        var method = message["method"]?.GetValue<string>();
        var parameters = message["params"];
        switch (method)
        {
            case "pres":
                var id = parameters?["id"]?.GetValue<long>() ?? 0;
                var ok = parameters?["ok"]?.GetValue<bool>() ?? false;
                Promises.Settle(id, ok, NodeMarshal.Unmarshal(this, parameters?["result"]));
                break;
            case "shim/stdout":
                OnStdOut?.Invoke(parameters?["text"]?.GetValue<string>() ?? "");
                break;
            case "shim/stderr":
                OnStdErr?.Invoke(parameters?["text"]?.GetValue<string>() ?? "");
                break;
            case "shim/error":
                RootContext?.Logger.Error("%s", parameters?["message"]?.GetValue<string>() ?? "unknown shim error");
                break;
        }
    }

    private async Task<JsonNode?> HandleRequest(JsonNode message)
    {
        var method = message["method"]!.GetValue<string>();
        var parameters = message["params"] as JsonObject ?? new JsonObject();
        var result = method switch
        {
            "hget" => ContextRpc.HandleGet(this, parameters),
            "hset" => ContextRpc.HandleSet(this, parameters),
            "hhas" => ContextRpc.HandleHas(this, parameters),
            "hcall" => await ContextRpc.HandleCall(this, parameters),
            "ccall" => await ContextRpc.HandleContextCall(this, parameters),
            _ => throw new JsRemoteException($"unknown method {method}", null),
        };
        return NodeMarshal.Marshal(this, result);
    }

    public void Dispose()
    {
        _rpc.Dispose();
        try
        {
            _process.Kill();
        }
        catch
        {
            // 进程已退出
        }
        _process.Dispose();
    }
}
