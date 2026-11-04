using System.Buffers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Sandbox;
using Dsh.Transport;

namespace Dsh.Plugins.Sandbox;

/** Implements the same file, HTTP and tool capabilities for local and isolated plugin SDKs. */
public sealed class PluginCapabilityFactory(Context context, IPluginExecutionEnvironment environment)
    : IPluginHostFactory, IDisposable
{
    internal const int MaximumChunkBytes = 256 * 1024;
    internal const int MaximumRegistrations = PluginHostOperations.MaximumTools;
    private const int CopyBufferBytes = 16 * 1024;
    private readonly HttpClient _http = new(context.GetProp(OutboundProxy.PropName) is OutboundProxy proxy
        ? proxy.CreateHandler(useCookies: false)
        : new HttpClientHandler { UseCookies = false, UseProxy = false });

    public IPluginHost Create(string package, Context pluginContext)
    {
        var host = new LocalHost(this, package, pluginContext);
        pluginContext.Effect(() => host, IPluginHostFactory.ServiceName);
        return host;
    }


    internal ValueTask<JsonElement> ExecuteAsync(string package, string operation, JsonElement arguments,
        ToolRunContext? execution, CancellationToken cancellationToken)
        => operation switch
        {
            PluginHostOperations.ReadFile => ReadFileAsync(package, arguments, execution, cancellationToken),
            PluginHostOperations.WriteFile => WriteFileAsync(package, arguments, execution, cancellationToken),
            PluginHostOperations.SendHttp => SendHttpAsync(package, arguments, cancellationToken),
            _ => throw new RuntimeException("PLUGIN_HOST_OPERATION_UNKNOWN", "The plugin requested an unsupported host operation."),
        };

    private bool Require(string package, PluginPermission permission)
    {
        var active = environment.ReadSettings();
        var configured = active.Plugins.GetValueOrDefault(package)?.Permissions ?? PluginPermissions.AllAllowed;
        if (!configured.Allows(permission))
            throw new RuntimeException("PLUGIN_PERMISSION_DENIED", $"The plugin's {PluginPermissions.KeyOf(permission)} permission is denied.");
        return configured.Network;
    }

    private string ResolvePath(JsonElement arguments, ToolRunContext? execution, bool allowNetwork)
    {
        var path = arguments.GetProperty("path").GetString()
            ?? throw new ArgumentException("The file operation requires a path.");
        global::Dsh.Transport.FilePathAccess.DemandFilePath(path);
        var cwd = execution?.Agent?.Session.WorkingDirectory
            ?? environment.WorkingDirectory;
        return global::Dsh.Transport.FilePathAccess.Canonicalize(Path.GetFullPath(path, cwd), allowNetwork);
    }

    private async ValueTask<JsonElement> ReadFileAsync(string package, JsonElement arguments,
        ToolRunContext? execution, CancellationToken cancellationToken)
    {
        var allowNetwork = Require(package, PluginPermission.Read);
        var path = ResolvePath(arguments, execution, allowNetwork);
        SandboxFileAccess.DemandOrdinaryFile(path, allowMissing: false);
        var offset = arguments.TryGetProperty("offset", out var position) ? position.GetInt64() : 0;
        var count = arguments.TryGetProperty("count", out var requested) ? requested.GetInt32() : MaximumChunkBytes;
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumChunkBytes);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            CopyBufferBytes, FileOptions.Asynchronous);
        stream.Position = offset;
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            var result = new PluginFileReadResult(Convert.ToBase64String(buffer, 0, read), stream.Length,
                read == 0 || offset + read >= stream.Length);
            return JsonSerializer.SerializeToElement(result, PluginBrokerJsonContext.Default.PluginFileReadResult);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async ValueTask<JsonElement> WriteFileAsync(string package, JsonElement arguments,
        ToolRunContext? execution, CancellationToken cancellationToken)
    {
        var allowNetwork = Require(package, PluginPermission.Write);
        var path = ResolvePath(arguments, execution, allowNetwork);
        SandboxFileAccess.DemandOrdinaryFile(path, allowMissing: true);
        var gate = context.Get<ISettingsMutationGate>(ISettingsMutationGate.ServiceName, false);
        var data = DecodeBody(arguments, "data");
        var append = arguments.TryGetProperty("append", out var appending) && appending.GetBoolean();
        if (gate?.Owns(path) == true || FilePathAccess.SameFile(path, environment.SettingsPath))
            await WriteSettingsAsync(package, path, data, append, execution, gate, cancellationToken).ConfigureAwait(false);
        else
            await WriteBytesAsync(path, data, append, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new PluginFileWriteResult(data.Length),
            PluginBrokerJsonContext.Default.PluginFileWriteResult);
    }

    private async Task WriteSettingsAsync(string package, string path, byte[] data, bool append, ToolRunContext? execution,
        ISettingsMutationGate? gate, CancellationToken cancellationToken)
    {
        if (gate is null || execution is null)
            throw new RuntimeException("SETTINGS_APPROVAL_UNAVAILABLE", "Settings changes require an active tool call and the host settings approval service.");
        var target = gate.SettingsPath;
        var before = File.Exists(target) ? await SettingsMutation.ReadTextAsync(target, cancellationToken).ConfigureAwait(false) : "";
        var text = Encoding.UTF8.GetString(data);
        var after = append ? $"{before}{text}" : text;
        await gate.CommitAsync(new SettingsMutationRequest(path, before, after, execution), (temporary, signal) =>
        {
            Require(package, PluginPermission.Write);
            return WriteBytesAsync(temporary, Encoding.UTF8.GetBytes(after), append: false, signal);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBytesAsync(string path, ReadOnlyMemory<byte> data, bool append, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions
        {
            Mode = append ? FileMode.Append : FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> SendHttpAsync(string package, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        Require(package, PluginPermission.Network);
        var url = arguments.GetProperty("url").GetString() ?? throw new ArgumentException("The HTTP operation requires a URL.");
        var method = arguments.TryGetProperty("method", out var requested) ? requested.GetString() : null;
        using var request = new HttpRequestMessage(new HttpMethod(method ?? "GET"), url);
        if (arguments.TryGetProperty("body", out _))
            request.Content = new ByteArrayContent(DecodeBody(arguments, "body"));
        AddHeaders(request, arguments);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var body = await ReadResponseAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
            headers[header.Key] = header.Value.ToArray();
        foreach (var header in response.Content.Headers)
            headers[header.Key] = header.Value.ToArray();
        return JsonSerializer.SerializeToElement(new PluginHttpResult((int)response.StatusCode, headers, body),
            PluginBrokerJsonContext.Default.PluginHttpResult);
    }

    private static void AddHeaders(HttpRequestMessage request, JsonElement arguments)
    {
        if (!arguments.TryGetProperty("headers", out var headers))
            return;
        foreach (var header in headers.EnumerateObject())
        {
            var values = header.Value.ValueKind == JsonValueKind.Array
                ? header.Value.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
                : [header.Value.GetString() ?? ""];
            if (!request.Headers.TryAddWithoutValidation(header.Name, values))
            {
                request.Content ??= new ByteArrayContent([]);
                if (!request.Content.Headers.TryAddWithoutValidation(header.Name, values))
                    throw new ArgumentException("The HTTP request contains an unsupported header.");
            }
        }
    }

    private static async Task<string> ReadResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumChunkBytes)
            throw new RuntimeException("PLUGIN_HTTP_RESPONSE_TOO_LARGE", "The HTTP response exceeds the bounded broker response size.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > MaximumChunkBytes)
                    throw new RuntimeException("PLUGIN_HTTP_RESPONSE_TOO_LARGE", "The HTTP response exceeds the bounded broker response size.");
                output.Write(buffer, 0, read);
            }
            return Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static byte[] DecodeBody(JsonElement arguments, string key)
    {
        var encoded = arguments.GetProperty(key).GetString() ?? throw new ArgumentException("File and HTTP bodies must be base64 strings.");
        return Convert.FromBase64String(encoded);
    }

    public void Dispose() => _http.Dispose();

    private sealed class LocalHost(PluginCapabilityFactory factory, string package, Context pluginContext)
        : IPluginHost, IPluginExecutionHost, IDisposable
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<int, ToolLease> _registrations = [];
        private int _nextHandle;
        private readonly AsyncLocal<Invocation?> _current = new();
        private readonly CancellationTokenSource _lifetime = new();
        private bool _disposed;

        public IDisposable RegisterTool(PluginToolDefinition definition)
            => RegisterTool(definition, (arguments, run, signal) => InvokeDefinitionAsync(definition, arguments, run, signal));

        public IDisposable RegisterTool(PluginToolDefinition definition,
            Func<JsonElement, ToolRunContext, CancellationToken, ValueTask<JsonElement>> execute)
        {
            var schema = definition.Parameters.GetRawText();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_registrations.Count >= MaximumRegistrations)
                    throw new RuntimeException("PLUGIN_REGISTRATION_LIMIT", "The plugin exceeded the tool count limit.");
                var tools = pluginContext.Get<ToolRuntime>(ToolRuntime.ServiceName)
                    ?? throw new InvalidOperationException("The tools service is required to register plugin tools.");
                var registration = tools.RegisterForPlugin(package, CreateDefinition(definition, schema, execute));
                var handle = checked(++_nextHandle);
                var lease = new ToolLease(this, handle, registration);
                _registrations.Add(handle, lease);
                return lease;
            }
        }

        private static ToolDefinition CreateDefinition(PluginToolDefinition definition, string schema,
            Func<JsonElement, ToolRunContext, CancellationToken, ValueTask<JsonElement>> execute) => new()
        {
            Name = definition.Name,
            Description = definition.Description,
            Parameters = JsonNode.Parse(schema)?.AsObject() ?? throw new ArgumentException("Plugin parameters must be an object schema."),
            Output = new ToolOutputDefinition([], (_, value) => [new TextBlock(value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : value.GetRawText())]),
            Execute = async (arguments, execution) => await execute(arguments, execution, execution.Signal).ConfigureAwait(false),
        };

        private async ValueTask<JsonElement> InvokeDefinitionAsync(PluginToolDefinition definition, JsonElement arguments,
            ToolRunContext run, CancellationToken signal)
        {
            Invocation invocation;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                invocation = new Invocation(run, CancellationTokenSource.CreateLinkedTokenSource(signal, _lifetime.Token));
            }
            var previous = _current.Value;
            _current.Value = invocation;
            try
            {
                invocation.Cancellation.Token.ThrowIfCancellationRequested();
                var result = await definition.Execute(arguments, invocation.Cancellation.Token).ConfigureAwait(false);
                invocation.Cancellation.Token.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                _current.Value = previous;
                lock (_sync)
                {
                    invocation.Active = false;
                    invocation.Execution = null;
                }
                try { invocation.Cancellation.Cancel(); }
                finally { invocation.Cancellation.Dispose(); }
            }
        }

        public async ValueTask<JsonElement> CallAsync(string operation, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            ToolRunContext? run;
            CancellationTokenSource linked;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var invocation = _current.Value;
                run = invocation is { Active: true } ? invocation.Execution : null;
                linked = run is null
                    ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken)
                    : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken, invocation!.Cancellation.Token);
            }
            using (linked)
                return await factory.ExecuteAsync(package, operation, arguments, run, linked.Token).ConfigureAwait(false);
        }

        public async ValueTask<JsonElement> CallAsync(string operation, JsonElement arguments, ToolRunContext? run, CancellationToken cancellationToken)
        {
            CancellationTokenSource linked;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                linked = run is null
                    ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken)
                    : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken, run.Signal);
            }
            using (linked)
                return await factory.ExecuteAsync(package, operation, arguments, run, linked.Token).ConfigureAwait(false);
        }

        public void Log(int level, string message)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var logger = pluginContext.LoggerFor(package);
            switch (level)
            {
                case 0: logger.Debug("%s", message); break;
                case 2: logger.Warn("%s", message); break;
                case 3: logger.Error("%s", message); break;
                default: logger.Info("%s", message); break;
            }
        }

        private void Release(int handle)
        {
            lock (_sync)
            {
                _registrations.Remove(handle);
            }
        }

        public void Dispose()
        {
            ToolLease[] leases;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                leases = _registrations.Values.ToArray();
                _registrations.Clear();
            }
            try { _lifetime.Cancel(); }
            finally
            {
                _lifetime.Dispose();
                foreach (var lease in leases)
                    lease.Dispose();
            }
        }

        private sealed class Invocation(ToolRunContext execution, CancellationTokenSource cancellation)
        {
            internal ToolRunContext? Execution { get; set; } = execution;
            internal CancellationTokenSource Cancellation { get; } = cancellation;
            internal bool Active { get; set; } = true;
        }

        private sealed class ToolLease(LocalHost owner, int handle, IDisposable registration) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                owner.Release(handle);
                registration.Dispose();
            }
        }
    }

}

internal sealed record PluginFileReadResult(string Data, long Length, bool Eof);
internal sealed record PluginFileWriteResult(int BytesWritten);
internal sealed record PluginHttpResult(int Status, Dictionary<string, string[]> Headers, string Body);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PluginFileReadResult))]
[JsonSerializable(typeof(PluginFileWriteResult))]
[JsonSerializable(typeof(PluginHttpResult))]
internal sealed partial class PluginBrokerJsonContext : JsonSerializerContext;
