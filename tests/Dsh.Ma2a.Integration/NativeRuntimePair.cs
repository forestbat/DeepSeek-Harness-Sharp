using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Ma2a.Integration;

internal sealed class NativeRuntimePair : IAsyncDisposable
{
    private const int RunIdLength = 8;
    private const int MaximumUnixSocketPathBytes = 104;
    private const int MaximumSupervisorSocketPathBytes = 103;
    private const int ProcessIdentifierHexLength = 32;
    private readonly string _binary;
    private readonly string _targetState;
    private readonly string _callerState;
    private bool _targetAttempted;
    private bool _callerAttempted;

    private NativeRuntimePair(string binary, string root)
    {
        _binary = binary;
        Root = root;
        _targetState = Path.Combine(root, "a");
        _callerState = Path.Combine(root, "b");
        Target = new Ma2aClient(_targetState);
        Caller = new Ma2aClient(_callerState);
    }

    public string Root { get; }
    public string BinaryPath => _binary;
    public string TargetStateDirectory => _targetState;
    public string CallerStateDirectory => _callerState;
    public Ma2aClient Target { get; }
    public Ma2aClient Caller { get; }
    public string TargetId { get; private set; } = string.Empty;
    public string CallerId { get; private set; } = string.Empty;
    public string SpaceId { get; set; } = string.Empty;

    public static async Task<NativeRuntimePair> StartAsync(string binary, string stateRoot, CancellationToken cancellationToken)
    {
        ValidatePaths(binary, stateRoot);
        var pair = new NativeRuntimePair(binary, Path.Combine(stateRoot, Guid.NewGuid().ToString("N")[..RunIdLength]));
        Check(!Directory.Exists(pair.Root), "The randomly selected acceptance directory already exists; nothing was started.");
        Directory.CreateDirectory(pair.Root);
        await File.WriteAllTextAsync(Path.Combine(pair.Root, ".gitignore"), "*\n", cancellationToken);
        try { await pair.StartRuntimesAsync(cancellationToken); return pair; }
        catch { await pair.DisposeAsync(); throw; }
    }

    private static void ValidatePaths(string binary, string stateRoot)
    {
        Check(Path.IsPathFullyQualified(binary) && File.Exists(binary), "An existing absolute MA2A binary path is required.");
        Check(Path.IsPathFullyQualified(stateRoot), "An absolute repository-local state root is required.");
        var directory = new DirectoryInfo(Path.GetFullPath(stateRoot));
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".git")) && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;
        Check(directory is not null, "Acceptance state must stay inside a Git repository.");
        if (!OperatingSystem.IsWindows())
        {
            Check(Encoding.UTF8.GetByteCount(Path.Combine(stateRoot, new string('a', RunIdLength), "a", "run-v1", "control.sock")) < MaximumUnixSocketPathBytes, "Use a shorter repository-local state root for Unix sockets.");
            Check(Encoding.UTF8.GetByteCount(Path.Combine(stateRoot, new string('a', RunIdLength), "a", "run-v1", "p", $"{new string('a', ProcessIdentifierHexLength)}.sock")) <= MaximumSupervisorSocketPathBytes,
                "Use a shorter repository-local state root: durable Process supervisor socket paths must fit within 103 UTF-8 bytes.");
        }
    }

    private async Task StartRuntimesAsync(CancellationToken cancellationToken)
    {
        _targetAttempted = true;
        await LifecycleAsync(_targetState, "start", cancellationToken);
        _callerAttempted = true;
        await LifecycleAsync(_callerState, "start", cancellationToken);
        TargetId = Payload(await Target.CallAsync(Command("endpoint_info"), cancellationToken)).GetProperty("endpoint_id").GetString()!;
        CallerId = Payload(await Caller.CallAsync(Command("endpoint_info"), cancellationToken)).GetProperty("endpoint_id").GetString()!;
        Check(TargetId != CallerId, "Independent Runtime identities must differ.");
        await PassedAsync("Two task-owned Runtime identities started.");
    }

    internal async Task LifecycleAsync(string stateDirectory, string operation, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_binary) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "--state-dir", stateDirectory, operation }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The MA2A lifecycle process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        Check(process.ExitCode == 0, $"MA2A {operation} failed with exit code {process.ExitCode}; private output was withheld.");
    }

    public async Task RestartCallerAsync(CancellationToken cancellationToken)
        => await LifecycleAsync(_callerState, "restart", cancellationToken);

    public async Task GrantAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var grant = Mutation("space_grant");
        grant["space_id"] = SpaceId;
        grant["peer_endpoint_id"] = CallerId;
        foreach (var capability in new[] { "files_read", "files_write", "exec", "shell", "proxy", "process", "agent", "service" }) grant[capability] = enabled;
        var response = Payload(await Target.CallAsync(grant, cancellationToken));
        Check(response.GetProperty("files_read").GetBoolean() == enabled, "Grant response must report the signed capability change.");
    }

    public async Task<JsonElement> ExposeAsync(string kind, JsonObject? selector = null, CancellationToken cancellationToken = default)
    {
        var command = Mutation("service_expose");
        command["space_id"] = SpaceId;
        command["kind"] = kind;
        foreach (var name in new[] { "root", "path", "access", "host", "port_start", "port_end", "agent", "service" }) command[name] = null;
        if (selector is not null)
            foreach (var property in selector) command[property.Key] = property.Value?.DeepClone();
        return await Target.CallAsync(command, cancellationToken);
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var command = Mutation("control_sync_trigger");
        command["peer_endpoint_id"] = TargetId;
        await Caller.CallAsync(command, cancellationToken);
    }

    public static JsonObject Command(string operation) => new() { ["operation"] = operation };
    public static JsonObject Mutation(string operation)
        => new() { ["operation"] = operation, ["request_id"] = Guid.NewGuid().ToString("N") };
    public static JsonElement Payload(JsonElement response) => response.GetProperty("result").GetProperty("payload");
    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    public static Task PassedAsync(string scenario) => Console.Out.WriteLineAsync($"PASS {scenario}");

    public async ValueTask DisposeAsync()
    {
        var errors = new List<Exception>();
        if (_callerAttempted) await StopAsync(_callerState, errors);
        if (_targetAttempted) await StopAsync(_targetState, errors);
        if (errors.Count != 0) throw new AggregateException("Task-owned Runtime shutdown failed.", errors);
    }

    private async Task StopAsync(string stateDirectory, List<Exception> errors)
    {
        try { await LifecycleAsync(stateDirectory, "stop", CancellationToken.None); }
        catch (Exception error) { errors.Add(error); }
    }
}
