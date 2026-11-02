using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dsh.Plugins;

[assembly: DshPlugin("test/isolated-alpha")]
[assembly: DshPlugin("test/isolated-beta")]

namespace Dsh.IsolatedPluginSample;

internal static class ProcessProbe
{
    private const string MainExecutionMarkerKey = "Dsh.IsolatedPluginSample.MainExecutionMarker";
    internal static readonly Guid Instance = Guid.NewGuid();

#pragma warning disable CA2255 // Deliberately adversarial initialization, in an independently loaded test artifact.
    [ModuleInitializer]
    internal static void Initialize()
    {
        TryWrite(typeof(ProcessProbe).Assembly.Location + ".module-marker", "module executed");
    }
#pragma warning restore CA2255

    internal static bool TryWrite(string path, string text)
    {
        try
        {
            if (AppContext.GetData(MainExecutionMarkerKey) is string mainMarker)
                File.AppendAllText(mainMarker, $"{text}\n");
            File.WriteAllText(path, text);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

public sealed class Plugin : IDshIsolatedPlugin
{
    private readonly string _name;
    private readonly int _constructorProcessId = Environment.ProcessId;
    private int _initializationProcessId;
    private JsonElement _configuration;
    private TaskCompletionSource? _release;
    private Task<JsonElement>? _background;

    public Plugin(string package)
    {
        _name = package.EndsWith("alpha", StringComparison.Ordinal) ? "alpha" : "beta";
        ProcessProbe.TryWrite(typeof(Plugin).Assembly.Location + $".{_name}-constructor-marker", "constructor executed");
    }

    public ValueTask<IAsyncDisposable?> InitializeAsync(IPluginHost host, JsonElement configuration,
        CancellationToken cancellationToken = default)
    {
        _initializationProcessId = Environment.ProcessId;
        _configuration = configuration.Clone();
        ProcessProbe.TryWrite(typeof(Plugin).Assembly.Location + $".{_name}-initialize-marker", "initialize executed");
        var registration = host.RegisterTool(new PluginToolDefinition(
            "isolated_" + _name,
            "Exercise real isolated SDK capabilities and adversarial direct operating-system access.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    operation = new { type = "string" },
                    arguments = new { type = "object", additionalProperties = true },
                },
                required = new[] { "operation" },
            }),
            (arguments, signal) => ExecuteAsync(host, arguments, signal)));
        return ValueTask.FromResult<IAsyncDisposable?>(new Lifetime(registration));
    }

    private async ValueTask<JsonElement> ExecuteAsync(IPluginHost host, JsonElement input, CancellationToken signal)
    {
        ProcessProbe.TryWrite(typeof(Plugin).Assembly.Location + $".{_name}-tool-marker", "tool executed");
        var operation = input.GetProperty("operation").GetString();
        var arguments = input.TryGetProperty("arguments", out var value) ? value : JsonSerializer.SerializeToElement(new { });
        switch (operation)
        {
            case "identity":
                return JsonSerializer.SerializeToElement(new
                {
                    instance = ProcessProbe.Instance,
                    processId = Environment.ProcessId,
                    constructorProcessId = _constructorProcessId,
                    initializationProcessId = _initializationProcessId,
                    pidNamespace = OperatingSystem.IsLinux() ? new FileInfo("/proc/self/ns/pid").LinkTarget : null,
                });
            case "configuration":
                return _configuration;
            case "invalid-registration":
                try
                {
                    using var invalid = host.RegisterTool(new PluginToolDefinition("", "", JsonSerializer.SerializeToElement(new { }),
                        (_, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }))));
                    return JsonSerializer.SerializeToElement(new { rejected = false });
                }
                catch (Exception)
                {
                    return JsonSerializer.SerializeToElement(new { rejected = true });
                }
            case "oversized-log":
                try
                {
                    host.Log(1, new string('x', PluginHostOperations.MaximumFrameBytes));
                    return JsonSerializer.SerializeToElement(new { rejected = false });
                }
                catch (Exception)
                {
                    return JsonSerializer.SerializeToElement(new { rejected = true });
                }
            case "log-burst":
                for (var index = 0; index < 40; index++)
                    host.Log(1, "fixture log");
                return JsonSerializer.SerializeToElement(new { completed = true });
            case "stderr-burst":
                await Console.Error.WriteAsync(new string('x', 9000));
                await Console.Error.FlushAsync(signal);
                return JsonSerializer.SerializeToElement(new { completed = true });
            case "wait-after-write":
                await host.CallAsync(PluginHostOperations.WriteFile, arguments, signal);
                await Task.Delay(Timeout.InfiniteTimeSpan, signal);
                throw new InvalidOperationException("An infinite wait unexpectedly completed.");
            case "read":
                return await host.CallAsync(PluginHostOperations.ReadFile, arguments, signal);
            case "write":
                return await host.CallAsync(PluginHostOperations.WriteFile, arguments, signal);
            case "http":
                return await host.CallAsync(PluginHostOperations.SendHttp, arguments, signal);
            case "direct-write":
                return JsonSerializer.SerializeToElement(new { succeeded = ProcessProbe.TryWrite(arguments.GetProperty("path").GetString()!, "direct overwrite") });
            case "direct-read":
                try
                {
                    _ = await File.ReadAllTextAsync(arguments.GetProperty("path").GetString()!, signal);
                    return JsonSerializer.SerializeToElement(new { succeeded = true });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return JsonSerializer.SerializeToElement(new { succeeded = false });
                }
            case "native-write":
                return JsonSerializer.SerializeToElement(new { succeeded = NativeWrite(arguments.GetProperty("path").GetString()!) });
            case "direct-http":
                using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }))
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(signal))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        using var response = await client.PostAsync(arguments.GetProperty("url").GetString(), new StringContent("direct"), deadline.Token);
                        return JsonSerializer.SerializeToElement(new { succeeded = response.IsSuccessStatusCode });
                    }
                    catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
                    {
                        return JsonSerializer.SerializeToElement(new { succeeded = false });
                    }
                }
            case "crash-after-write":
                await host.CallAsync(PluginHostOperations.WriteFile, arguments, signal);
                Environment.Exit(73);
                throw new InvalidOperationException("Process exit unexpectedly returned.");
            case "arm-background-write":
                if (_background is not null)
                    throw new InvalidOperationException("Only one background attempt may be armed.");
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = release;
                var detachedArguments = arguments.Clone();
                _background = Task.Run(async () =>
                {
                    await release.Task;
                    try
                    {
                        await host.CallAsync(PluginHostOperations.WriteFile, detachedArguments, CancellationToken.None);
                        return JsonSerializer.SerializeToElement(new { committed = true });
                    }
                    catch (Exception)
                    {
                        return JsonSerializer.SerializeToElement(new { committed = false });
                    }
                }, CancellationToken.None);
                return JsonSerializer.SerializeToElement(new { armed = true });
            case "release-background-write":
                if (_release is null || _background is null)
                    throw new InvalidOperationException("No background attempt was armed.");
                _release.SetResult();
                return await _background.WaitAsync(signal);
            default:
                throw new ArgumentException("Unknown fixture operation.");
        }
    }

    private static bool NativeWrite(string path)
    {
        var bytes = Encoding.UTF8.GetBytes("native overwrite");
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(path, 0x40000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1))
                return false;
            try { return WriteFile(handle, bytes, (uint)bytes.Length, out var written, IntPtr.Zero) && written == bytes.Length; }
            finally { CloseHandle(handle); }
        }
        var fd = Open(path, 1);
        if (fd < 0)
            return false;
        try { return Write(fd, bytes, (nuint)bytes.Length) == bytes.Length; }
        finally { Close(fd); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint Write(int fd, byte[] bytes, nuint count);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr handle, byte[] bytes, uint count, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class Lifetime(IDisposable registration) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            registration.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
