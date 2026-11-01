using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Dsh.Sandbox;

/// <summary>Seatbelt enforces filesystem/network policy; memory is measured and supervised, not a kernel hard cap.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacSandbox : ISandboxLifetime
{
    private readonly UnixChildProcess _child;
    private readonly long _memoryLimit;
    private readonly Func<int, bool> _inspectMemory;
    private int _memoryExceeded;
    public int Id => _child.Id;
    public Stream StandardInput => _child.StandardInput;
    public Stream StandardOutput => _child.StandardOutput;
    public Stream StandardError => _child.StandardError;
    public bool WasMemoryLimitExceeded => Volatile.Read(ref _memoryExceeded) != 0;
    public Task<int> Exit { get; }

    private MacSandbox(UnixChildProcess child, long memoryLimit)
    {
        _child = child;
        _memoryLimit = memoryLimit;
        _inspectMemory = pid => ResidentBytes(pid) > (ulong)_memoryLimit;
        Exit = SuperviseAsync();
    }

    public static MacSandbox Start(SandboxPolicy policy)
    {
        const string launcher = "/usr/bin/sandbox-exec";
        const string environmentHelper = "/usr/bin/env";
        if (!File.Exists(launcher) || !File.Exists(environmentHelper))
            throw new PlatformNotSupportedException("macOS sandboxing requires sandbox-exec and the system env executable; no unrestricted fallback or automatic installation is used.");
        var profile = new StringBuilder("(version 1)\n(deny default)\n(allow process-exec (literal \"/usr/bin/env\") (literal ").Append(Quote(policy.Executable)).Append("))\n");
        profile.Append("(allow sysctl-read)\n(allow mach-lookup (global-name \"com.apple.system.logger\"))\n");
        // dyld's shared-cache bootstrap opens the root directory before the target executable starts.
        profile.Append("(allow file-read-data (require-all (literal \"/\") (vnode-type DIRECTORY)))\n");
        profile.Append("(allow file-read-metadata)\n(allow file-read* file-map-executable (literal \"/usr/bin/env\") (literal \"/dev/null\") (literal \"/dev/urandom\") (literal \"/dev/random\") (subpath \"/System/Library\") (subpath \"/usr/lib\"))\n");
        profile.Append("(allow file-write-data (literal \"/dev/null\"))\n");
        foreach (var read in policy.Readable)
        {
            profile.Append("(allow file-read* file-map-executable ").Append(Filter(read)).Append(")\n");
            // Image replacement stays in the owned PID and requires an explicit non-credential-changing file grant.
            if (!read.IsDirectory && read.Source != policy.Executable)
            {
                var mode = File.GetUnixFileMode(read.Source);
                if ((mode & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) == 0
                    && (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    profile.Append("(allow process-exec (literal ").Append(Quote(read.Source)).Append("))\n");
            }
            if (policy.Writable.Any(write => SandboxPolicy.Contains(write.Source, read.Source)))
                profile.Append("(deny file-write* ").Append(Filter(read)).Append(")\n");
        }
        foreach (var write in policy.Writable)
            profile.Append("(allow file-read* file-write* ").Append(Filter(write)).Append(")\n");
        // New hardlinks are denied, and pre-existing aliases of read-only objects receive explicit write denials.
        profile.Append("(deny file-link)\n(deny process-fork)\n(deny process-info-setcontrol)\n");
        foreach (var deny in policy.Denied.Concat(policy.Readable.Select(static read => read.Source)).Concat(policy.ProtectedAliases.Select(static alias => alias.Source)))
        {
            profile.Append("(deny file-write* (literal ").Append(Quote(deny)).Append("))\n");
            for (var parent = Path.GetDirectoryName(deny); parent is not null; parent = Path.GetDirectoryName(parent))
                if (policy.Writable.Any(write => SandboxPolicy.Contains(write.Source, parent)))
                    profile.Append("(deny file-write-unlink (literal ").Append(Quote(parent)).Append("))\n");
        }
        if (policy.AllowNetwork)
            profile.Append("(allow network*)\n");
        var arguments = new List<string> { "-p", profile.ToString(), environmentHelper, "-i" };
        arguments.AddRange(policy.EnvironmentEntries);
        arguments.Add(policy.Executable);
        arguments.AddRange(policy.Arguments);
        var child = UnixChildProcess.Start(launcher, arguments, policy.WorkingDirectory, SandboxEnvironment.LauncherEntries);
        return new MacSandbox(child, policy.MemoryLimitBytes);
    }

    private static string Filter(SandboxPath path) => $"({(path.IsDirectory ? "subpath" : "literal")} {Quote(path.Source)})";
    private static string Quote(string text) => $"\"{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)}\"";

    private async Task<int> SuperviseAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        try
        {
            while (!_child.Exit.IsCompleted)
            {
                if (_child.InspectWhileOwned(_inspectMemory))
                {
                    Interlocked.Exchange(ref _memoryExceeded, 1);
                    _child.Kill();
                }
                await timer.WaitForNextTickAsync().ConfigureAwait(false);
            }
            return await _child.Exit.ConfigureAwait(false);
        }
        catch
        {
            // Loss of measurement must not leave an unsupervised process running.
            _child.Kill();
            await _child.Exit.ConfigureAwait(false);
            throw;
        }
    }

    private static unsafe ulong ResidentBytes(int pid)
    {
        // PROC_PIDTASKINFO starts with virtual_size and resident_size (two uint64_t values).
        const int taskInfoSize = 96;
        var info = stackalloc byte[taskInfoSize];
        var count = proc_pidinfo(pid, 4, 0, info, taskInfoSize);
        if (count == taskInfoSize)
            return *(ulong*)(info + sizeof(ulong));
        var error = Marshal.GetLastPInvokeError();
        if (error == 3)
            return 0; // ESRCH: the owned, not-yet-reaped process has already exited.
        throw new Win32Exception(error, "Cannot measure the owned macOS sandbox process.");
    }

    public void Kill() => _child.Kill();
    public async ValueTask DisposeAsync()
    {
        Kill();
        try { await Exit.ConfigureAwait(false); }
        finally { await _child.DisposeAsync().ConfigureAwait(false); }
    }

    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    private static extern unsafe int proc_pidinfo(int pid, int flavor, ulong argument, byte* buffer, int size);
}
