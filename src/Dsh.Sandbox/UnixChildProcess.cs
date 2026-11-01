using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

/// <summary>Uses spawn file actions rather than a managed post-fork callback, and serializes reaping with PID-directed signals.</summary>
internal sealed class UnixChildProcess : IAsyncDisposable
{
    private const int OCloseOnExecLinux = 0x80000;
    private readonly Lock _gate = new();
    private bool _exited;
    public int Id { get; }
    public Stream StandardInput { get; }
    public Stream StandardOutput { get; }
    public Stream StandardError { get; }
    public Task<int> Exit { get; }

    private UnixChildProcess(int pid, Stream input, Stream output, Stream error)
    {
        Id = pid;
        StandardInput = input;
        StandardOutput = output;
        StandardError = error;
        Exit = ReapAsync();
    }

    public static UnixChildProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory, string[] environmentEntries, byte[]? seccomp = null, IReadOnlyList<SafeFileHandle>? mountPins = null)
    {
        using var input = UnixPipe.Create();
        using var output = UnixPipe.Create();
        using var error = UnixPipe.Create();
        using var filter = seccomp is null ? null : CreateSeccompFile(seccomp);
        using var passed = new SpawnDescriptors(filter, mountPins);
        using var memory = new SpawnMemory();
        var actions = memory.Allocate(1024);
        var attributes = memory.Allocate(1024);
        Check(posix_spawn_file_actions_init(actions), "initialize file actions");
        try
        {
            Check(posix_spawnattr_init(attributes), "initialize spawn attributes");
            try
            {
                Check(posix_spawn_file_actions_adddup2(actions, input.Read, 0), "redirect stdin");
                Check(posix_spawn_file_actions_adddup2(actions, output.Write, 1), "redirect stdout");
                Check(posix_spawn_file_actions_adddup2(actions, error.Write, 2), "redirect stderr");
                Check(posix_spawn_file_actions_addchdir_np(actions, memory.Text(workingDirectory)), "set working directory");
                // Darwin's close-on-exec default and glibc's closefrom action exclude every non-stdio descriptor.
                if (OperatingSystem.IsLinux())
                {
                    for (var index = 0; index < passed.Handles.Count; index++)
                        Check(posix_spawn_file_actions_adddup2(actions, checked((int)passed.Handles[index].DangerousGetHandle()), 3 + index), "pass an owned sandbox descriptor");
                    Check(posix_spawn_file_actions_addclosefrom_np(actions, 3 + passed.Handles.Count), "close unrelated descriptors");
                }
                else
                    Check(posix_spawnattr_setflags(attributes, 0x4000), "exclude unrelated descriptors");
                var argv = memory.Strings(new[] { executable }.Concat(arguments));
                // Only the explicitly validated child environment is passed; the ambient host environment is never inherited.
                var environment = memory.Strings(environmentEntries);
                Check(posix_spawn(out var pid, memory.Text(executable), actions, attributes, argv, environment), "spawn sandbox launcher");
                Stream? inputStream = null, outputStream = null, errorStream = null;
                try
                {
                    inputStream = input.TakeWriteStream();
                    outputStream = output.TakeReadStream();
                    errorStream = error.TakeReadStream();
                    return new UnixChildProcess(pid, inputStream, outputStream, errorStream);
                }
                catch
                {
                    kill(pid, 9);
                    while (waitpid(pid, out _, 0) < 0 && Marshal.GetLastPInvokeError() == 4) { }
                    inputStream?.Dispose();
                    outputStream?.Dispose();
                    errorStream?.Dispose();
                    throw;
                }
            }
            finally { posix_spawnattr_destroy(attributes); }
        }
        finally { posix_spawn_file_actions_destroy(actions); }
    }

    private static SafeFileHandle CreateSeccompFile(byte[] program)
    {
        var descriptor = memfd_create("dsh-seccomp", 1);
        if (descriptor < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create the anonymous sandbox seccomp program.");
        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            RandomAccess.Write(handle, program, 0);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    public void Kill()
    {
        lock (_gate)
        {
            if (!_exited)
                kill(Id, 9);
        }
    }

    public bool InspectWhileOwned(Func<int, bool> inspect)
    {
        lock (_gate)
            return !_exited && inspect(Id);
    }

    private async Task<int> ReapAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        while (true)
        {
            lock (_gate)
            {
                var result = waitpid(Id, out var status, 1);
                if (result == Id)
                {
                    _exited = true;
                    return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
                }
                if (result < 0 && Marshal.GetLastPInvokeError() != 4)
                {
                    _exited = true;
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot reap the owned sandbox process.");
                }
            }
            await timer.WaitForNextTickAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try { await Exit.ConfigureAwait(false); }
        finally
        {
            await StandardInput.DisposeAsync().ConfigureAwait(false);
            await StandardOutput.DisposeAsync().ConfigureAwait(false);
            await StandardError.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void Check(int error, string operation)
    {
        if (error != 0)
            throw new Win32Exception(error, $"Cannot {operation}.");
    }

    private sealed class SpawnMemory : IDisposable
    {
        private readonly List<nint> _pointers = [];
        public nint Allocate(int bytes)
        {
            var pointer = Marshal.AllocHGlobal(bytes);
            unsafe { new Span<byte>((void*)pointer, bytes).Clear(); }
            _pointers.Add(pointer);
            return pointer;
        }
        public nint Text(string text)
        {
            var pointer = Marshal.StringToHGlobalAnsi(text);
            _pointers.Add(pointer);
            return pointer;
        }
        public nint Strings(IEnumerable<string> strings)
        {
            var values = strings.Select(Text).ToArray();
            var pointer = Allocate((values.Length + 1) * nint.Size);
            for (var index = 0; index < values.Length; index++)
                Marshal.WriteIntPtr(pointer, index * nint.Size, values[index]);
            return pointer;
        }
        public void Dispose()
        {
            foreach (var pointer in _pointers)
                Marshal.FreeHGlobal(pointer);
        }
    }

    private sealed class SpawnDescriptors : IDisposable
    {
        public List<SafeFileHandle> Handles { get; } = [];
        public SpawnDescriptors(SafeFileHandle? filter, IReadOnlyList<SafeFileHandle>? pins)
        {
            if (!OperatingSystem.IsLinux() && (filter is not null || pins is { Count: > 0 }))
                throw new PlatformNotSupportedException("Explicit mount and seccomp descriptors require Linux.");
            var count = (filter is null ? 0 : 1) + (pins?.Count ?? 0);
            try
            {
                if (filter is not null)
                    Add(filter, count);
                if (pins is not null)
                    foreach (var pin in pins)
                        Add(pin, count);
            }
            catch { Dispose(); throw; }
        }
        private void Add(SafeFileHandle source, int count)
        {
            var added = false;
            try
            {
                source.DangerousAddRef(ref added);
                // Stage sources above every dup2 destination so file actions cannot overwrite a later source.
                var duplicate = UnixFileInterop.Fcntl(checked((int)source.DangerousGetHandle()), 1030, checked(3 + count));
                if (duplicate < 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot duplicate an owned sandbox descriptor.");
                Handles.Add(new SafeFileHandle(duplicate, ownsHandle: true));
            }
            finally { if (added) source.DangerousRelease(); }
        }
        public void Dispose()
        {
            foreach (var handle in Handles)
                handle.Dispose();
        }
    }

    private sealed class UnixPipe : IDisposable
    {
        public int Read { get; private set; }
        public int Write { get; private set; }
        private UnixPipe(int read, int write) { Read = read; Write = write; }
        public static unsafe UnixPipe Create()
        {
            int* descriptors = stackalloc int[2];
            if (OperatingSystem.IsLinux())
            {
                if (pipe2(descriptors, OCloseOnExecLinux) != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create sandbox stdio pipe.");
            }
            else
            {
                if (pipe(descriptors) != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create sandbox stdio pipe.");
                if (UnixFileInterop.Fcntl(descriptors[0], 2, 1) < 0 || UnixFileInterop.Fcntl(descriptors[1], 2, 1) < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    close(descriptors[0]);
                    close(descriptors[1]);
                    throw new Win32Exception(error, "Cannot mark sandbox pipe close-on-exec.");
                }
            }
            return new UnixPipe(descriptors[0], descriptors[1]);
        }
        public Stream TakeWriteStream()
        {
            var handle = new SafeFileHandle(Write, ownsHandle: true);
            Write = -1;
            try { return new FileStream(handle, FileAccess.Write, 4096, isAsync: false); }
            catch { handle.Dispose(); throw; }
        }
        public Stream TakeReadStream()
        {
            var handle = new SafeFileHandle(Read, ownsHandle: true);
            Read = -1;
            try { return new FileStream(handle, FileAccess.Read, 4096, isAsync: false); }
            catch { handle.Dispose(); throw; }
        }
        public void Dispose()
        {
            if (Read >= 0) close(Read);
            if (Write >= 0) close(Write);
        }
    }

    [DllImport("libc")] private static extern int posix_spawn(out int pid, nint path, nint actions, nint attributes, nint argv, nint environment);
    [DllImport("libc", SetLastError = true)] private static extern int memfd_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_init(nint actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(nint actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(nint actions, int descriptor, int target);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addclosefrom_np(nint actions, int first);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addchdir_np(nint actions, nint path);
    [DllImport("libc")] private static extern int posix_spawnattr_init(nint attributes);
    [DllImport("libc")] private static extern int posix_spawnattr_destroy(nint attributes);
    [DllImport("libc")] private static extern int posix_spawnattr_setflags(nint attributes, short flags);
    [DllImport("libc", SetLastError = true)] private static extern unsafe int pipe2(int* descriptors, int flags);
    [DllImport("libc", SetLastError = true)] private static extern unsafe int pipe(int* descriptors);
    [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport("libc")] private static extern int close(int descriptor);
}
