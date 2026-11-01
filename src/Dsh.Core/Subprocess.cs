using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Dsh.Runtime;
using Dsh.Sandbox;
using Dsh.Transport;

namespace Dsh.Core;

public sealed record SubprocessCollect(int MaxBytes, int? SpillMaxBytes);

public sealed record SubprocessOutputRead(string Text, long NextOffset, bool Lossy, string? SpillPath);

public sealed record SubprocessOutcome(int? ExitCode, string? Signal);

/// <summary>Selected only by trusted launchers, never inferred from executable names or model arguments.</summary>
public enum SubprocessExecutionKind { Tool, Script, Mcp }

public sealed record SubprocessSpawnSpec
{
    public required IReadOnlyList<string> Argv { get; init; }
    public required string Cwd { get; init; }
    public IReadOnlyDictionary<string, string?>? Env { get; init; }
    public SubprocessCollect Stdout { get; init; } = new(64 * 1024, 64 * 1024 * 1024);
    public SubprocessCollect Stderr { get; init; } = new(64 * 1024, 64 * 1024 * 1024);
    /** 后台进程始终隔离终端; 只有管道协议调用方保留可写 stdin, 其余立即发送 EOF。 */
    public bool KeepStandardInputOpen { get; init; }
    public CancellationToken Signal { get; init; }
    public bool ModelExecution { get; init; }
    public SubprocessExecutionKind ExecutionKind { get; init; }
    public bool ProtocolOutput { get; init; }
    public IReadOnlyList<string> AdditionalWritablePaths { get; init; } = [];
    public IReadOnlyList<string> AdditionalReadOnlyPaths { get; init; } = [];
}

public sealed class SubprocessOutputReader
{
    private readonly StreamCollector _collector;

    internal SubprocessOutputReader(StreamCollector collector) => _collector = collector;

    public SubprocessOutputRead ReadFrom(long fromByte) => _collector.ReadFrom(fromByte);
}

internal sealed class StreamCollector : IDisposable
{
    private static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private readonly int _maxBytes;
    private readonly int? _spillMaxBytes;
    private readonly object _gate = new();
    private MemoryStream _tail = new();
    private FileStream? _spill;
    private string? _spillPath;
    private long _totalBytes;
    private bool _spillOverflowed;

    public StreamCollector(int maxBytes, int? spillMaxBytes)
    {
        _maxBytes = maxBytes;
        _spillMaxBytes = spillMaxBytes;
    }

    public long TotalBytes
    {
        get { lock (_gate) return _totalBytes; }
    }

    public void Push(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return;
        lock (_gate)
        {
            _totalBytes += chunk.Length;
            _tail.Write(chunk);
            if (_tail.Length > _maxBytes)
            {
                var excess = (int)(_tail.Length - _maxBytes);
                var kept = _tail.GetBuffer().AsSpan(excess, _maxBytes);
                var next = new MemoryStream(_maxBytes);
                next.Write(kept);
                _tail.Dispose();
                _tail = next;
            }
            if (_spillMaxBytes is not null && !_spillOverflowed)
            {
                _spill ??= CreateSpill(out _spillPath);
                if (_spill.Length + chunk.Length > _spillMaxBytes.Value)
                {
                    _spill.Dispose();
                    _spill = null;
                    _spillOverflowed = true;
                    TryDelete(_spillPath);
                    _spillPath = null;
                }
                else
                {
                    _spill.Write(chunk);
                }
            }
        }
    }

    public SubprocessOutputRead ReadFrom(long fromByte)
    {
        lock (_gate)
        {
            var retainedStart = _totalBytes - _tail.Length;
            var lossy = fromByte < retainedStart;
            var begin = (int)Math.Max(0, fromByte - retainedStart);
            var span = _tail.GetBuffer().AsSpan(begin, (int)_tail.Length - begin);
            return new SubprocessOutputRead(
                LenientUtf8.GetString(span),
                _totalBytes,
                lossy,
                _spillOverflowed ? null : _spillPath);
        }
    }

    public void Finish()
    {
        lock (_gate) _spill?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _spill?.Dispose();
            _tail.Dispose();
        }
    }

    private static FileStream CreateSpill(out string path)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dsh-spill");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, $"{Guid.NewGuid():N}.log");
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class SubprocessHandle : IDisposable
{
    private readonly Process? _process;
    private readonly SandboxProcess? _sandbox;
    private readonly StreamWriter _stdin;
    private readonly Stream _output;
    private readonly Stream _error;
    private readonly bool _protocolOutput;
    private readonly string? _temporaryDirectory;
    private readonly StreamCollector _stdout;
    private readonly StreamCollector _stderr;
    private readonly CancellationTokenRegistration _abortRegistration;
    private int _terminateRequested;
    private int _disposed;

    internal SubprocessHandle(Process process, StreamCollector stdout, StreamCollector stderr, CancellationToken signal)
    {
        _process = process;
        _stdout = stdout;
        _stderr = stderr;
        _stdin = process.StandardInput;
        _output = process.StandardOutput.BaseStream;
        _error = process.StandardError.BaseStream;
        Pid = process.Id;
        StdoutReader = new SubprocessOutputReader(stdout);
        StderrReader = new SubprocessOutputReader(stderr);
        Done = RunCompletion();
        if (signal.CanBeCanceled)
            _abortRegistration = signal.Register(Terminate);
    }

    internal SubprocessHandle(SandboxProcess process, StreamCollector stdout, StreamCollector stderr,
        SubprocessSpawnSpec spec, string temporaryDirectory)
    {
        _sandbox = process;
        _temporaryDirectory = temporaryDirectory;
        // Match Process.StandardInput: interactive callers send complete commands without an explicit flush.
        _stdin = new StreamWriter(process.StandardInput, new UTF8Encoding(false)) { AutoFlush = true };
        _output = process.StandardOutput;
        _error = process.StandardError;
        _protocolOutput = spec.ProtocolOutput;
        _stdout = stdout;
        _stderr = stderr;
        Pid = process.Id;
        StdoutReader = new SubprocessOutputReader(stdout);
        StderrReader = new SubprocessOutputReader(stderr);
        Done = RunCompletion();
        if (spec.Signal.CanBeCanceled)
            _abortRegistration = spec.Signal.Register(Terminate);
    }

    public int Pid { get; }

    public SubprocessOutputReader StdoutReader { get; }

    public SubprocessOutputReader StderrReader { get; }

    public StreamWriter StandardInput => _stdin;

    /// <summary>Available only to an explicit protocol owner; collectors never consume this stream.</summary>
    public Stream StandardOutput => _protocolOutput
        ? _output
        : throw new InvalidOperationException("subprocess: stdout belongs to the output collector");

    public Task<SubprocessOutcome> Done { get; }

    public void Terminate()
    {
        if (Interlocked.Exchange(ref _terminateRequested, 1) != 0) return;
        try
        {
            if (_sandbox is not null)
                _sandbox.Kill();
            else if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }
    }

    private async Task<SubprocessOutcome> RunCompletion()
    {
        var drainStdout = _protocolOutput ? Task.CompletedTask : PumpAsync(_output, _stdout);
        var drainStderr = PumpAsync(_error, _stderr);
        try
        {
            var exitCode = _sandbox is not null
                ? await _sandbox.WaitForExitAsync().ConfigureAwait(false)
                : await WaitForOperatorExitAsync().ConfigureAwait(false);
            // WaitForExitAsync includes owned descendant teardown but leaves pipes open for draining.
            await Task.WhenAll(drainStdout, drainStderr).ConfigureAwait(false);
            if (_sandbox?.WasMemoryLimitExceeded == true)
                return new SubprocessOutcome(null, "MEMORY_LIMIT");
            if (_terminateRequested != 0)
                return new SubprocessOutcome(null, "SIGKILL");
            if (exitCode > 128 && !OperatingSystem.IsWindows())
                return new SubprocessOutcome(null, SignalName(exitCode - 128));
            return new SubprocessOutcome(exitCode, null);
        }
        finally
        {
            if (_sandbox is not null && !_protocolOutput)
                await _sandbox.DisposeAsync().ConfigureAwait(false);
            _stdout.Finish();
            _stderr.Finish();
        }
    }

    private async Task<int> WaitForOperatorExitAsync()
    {
        await _process!.WaitForExitAsync().ConfigureAwait(false);
        return _process.ExitCode;
    }

    private static async Task PumpAsync(Stream stream, StreamCollector collector)
    {
        var buffer = new byte[8192];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return;
            }
            if (read == 0) return;
            collector.Push(buffer.AsSpan(0, read));
        }
    }

    internal static string SignalName(int number) => number switch
    {
        1 => "SIGHUP",
        2 => "SIGINT",
        9 => "SIGKILL",
        13 => "SIGPIPE",
        15 => "SIGTERM",
        _ => $"SIG{number}",
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _abortRegistration.Dispose();
        if (!Done.IsCompleted)
            Terminate();
        try
        {
            Done.GetAwaiter().GetResult();
        }
        finally
        {
            try
            {
                if (_sandbox is not null && _protocolOutput)
                    _sandbox.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                try { _stdin.Dispose(); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                _process?.Dispose();
                _stdout.Dispose();
                _stderr.Dispose();
                if (_temporaryDirectory is not null)
                    Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
    }
}

public sealed class SubprocessService : Service, IDisposable
{
    public const string ServiceName = "subprocess";
    public const string DshEnvPrefix = "DSH_";

    private static readonly Regex SensitiveEnvPattern = new("KEY|PASSWORD|SECRET|TOKEN", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly List<SubprocessHandle> _running = [];
    private readonly object _gate = new();

    public SubprocessService(Context ctx) : base(ctx, ServiceName)
    {
    }

    public static Dictionary<string, string> ScrubbedParentEnv()
    {
        var env = new Dictionary<string, string>();
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>())
        {
            if (SensitiveEnvPattern.IsMatch(key)) continue;
            if (key.StartsWith(DshEnvPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (Environment.GetEnvironmentVariable(key) is { } value)
                env[key] = value;
        }
        return env;
    }

    public Task<string> ResolveExecutable(string command, IReadOnlyDictionary<string, string>? env = null, CancellationToken signal = default)
    {
        if (Path.IsPathRooted(command))
        {
            if (!File.Exists(command))
                throw new FileNotFoundException($"subprocess: executable not found: \"{command}\"");
            return Task.FromResult(command);
        }
        if (command.Contains('/') || (OperatingSystem.IsWindows() && command.Contains('\\')))
            throw new ArgumentException($"subprocess: cannot resolve relative executable path \"{command}\"");
        var pathValue = (env is not null && env.TryGetValue("PATH", out var overridden) ? overridden : null)
            ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        var names = OperatingSystem.IsWindows() && !Path.HasExtension(command)
            ? new[] { command + ".exe", command + ".cmd", command + ".bat", command }
            : [command];
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            signal.ThrowIfCancellationRequested();
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return Task.FromResult(candidate);
            }
        }
        throw new FileNotFoundException($"subprocess: executable \"{command}\" not found on PATH");
    }
    public async Task<SubprocessHandle> SpawnAsync(SubprocessSpawnSpec spec)
    {
        if (!spec.ModelExecution && (spec.ExecutionKind != SubprocessExecutionKind.Tool || SettingsMutationScope.Current is not null))
            spec = spec with { ModelExecution = true };
        if (!spec.ModelExecution)
            return Spawn(spec);
        if (spec.Argv.Count == 0)
            throw new ArgumentException("subprocess: argv must be non-empty");
        spec.Signal.ThrowIfCancellationRequested();
        var policy = Ctx.Get<IExternalProcessPolicy>(IExternalProcessPolicy.ServiceName, false);
        if (policy?.Enabled != true)
            return SpawnDirect(spec);
        IReadOnlyDictionary<string, string>? executableEnvironment = null;
        if (spec.Env?.TryGetValue("PATH", out var searchPath) == true && searchPath is not null)
            executableEnvironment = new Dictionary<string, string> { ["PATH"] = searchPath };
        var executable = await ResolveExecutable(spec.Argv[0], executableEnvironment, spec.Signal).ConfigureAwait(false);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"dsh-process-{Guid.NewGuid():N}");
        OwnerOnlyFiles.EnsureDirectory(temporaryDirectory);
        SandboxProcess? process = null;
        try
        {
            var options = policy.CreateOptions(spec, executable, temporaryDirectory);
            process = await SandboxedProcess.StartAsync(options, spec.Signal).ConfigureAwait(false);
            var handle = new SubprocessHandle(process,
                new StreamCollector(spec.Stdout.MaxBytes, spec.Stdout.SpillMaxBytes),
                new StreamCollector(spec.Stderr.MaxBytes, spec.Stderr.SpillMaxBytes),
                spec, temporaryDirectory);
            if (!spec.KeepStandardInputOpen)
                handle.StandardInput.Close();
            Track(handle);
            return handle;
        }
        catch
        {
            if (process is not null)
                await process.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(temporaryDirectory, recursive: true);
            throw;
        }
    }


    public SubprocessHandle Spawn(SubprocessSpawnSpec spec)
    {
        if (spec.ModelExecution || spec.ExecutionKind != SubprocessExecutionKind.Tool || SettingsMutationScope.Current is not null)
            return SpawnAsync(spec.ModelExecution ? spec : spec with { ModelExecution = true }).GetAwaiter().GetResult();
        return SpawnDirect(spec);
    }

    private SubprocessHandle SpawnDirect(SubprocessSpawnSpec spec)
    {
        if (spec.ProtocolOutput)
            throw new InvalidOperationException("Protocol subprocesses require the owned model process boundary.");
        if (spec.Argv.Count == 0)
            throw new ArgumentException("subprocess: argv must be non-empty");
        spec.Signal.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.Argv[0],
            WorkingDirectory = spec.Cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in spec.Argv.Skip(1))
            startInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in ScrubbedParentEnv())
            startInfo.Environment[key] = value;
        if (spec.Env is not null)
        {
            foreach (var (key, value) in spec.Env)
            {
                if (value is null) startInfo.Environment.Remove(key);
                else startInfo.Environment[key] = value;
            }
        }
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"subprocess: failed to start \"{spec.Argv[0]}\"");
        if (!spec.KeepStandardInputOpen)
            process.StandardInput.Close();
        var handle = new SubprocessHandle(
            process,
            new StreamCollector(spec.Stdout.MaxBytes, spec.Stdout.SpillMaxBytes),
            new StreamCollector(spec.Stderr.MaxBytes, spec.Stderr.SpillMaxBytes),
            spec.Signal);
        Track(handle);
        return handle;
    }

    private void Track(SubprocessHandle handle)
    {
        lock (_gate) _running.Add(handle);
        _ = handle.Done.ContinueWith(_ =>
        {
            lock (_gate) _running.Remove(handle);
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    public void Dispose()
    {
        List<SubprocessHandle> running;
        lock (_gate) running = [.. _running];
        foreach (var handle in running)
            handle.Terminate();
        foreach (var handle in running)
            handle.Dispose();
    }
}
