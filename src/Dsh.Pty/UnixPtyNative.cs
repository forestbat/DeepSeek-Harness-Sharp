using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Pty;

internal static class UnixPtyNative
{
    private const ulong Tiocswinsz = 0x5414;
    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private const int Wnohang = 1;

    public static (int MasterFd, int Pid) Spawn(PtyStartInfo info)
    {
        var (master, slave) = OpenPty(info.Rows, info.Columns);
        var allocated = new List<IntPtr>();
        try
        {
            var file = AllocAnsi(info.FileName);
            allocated.Add(file);

            var (argv, argvStrings) = AllocStringArray(new[] { info.FileName }.Concat(info.Arguments).ToList());
            allocated.Add(argv);
            allocated.AddRange(argvStrings);

            var (envp, envStrings) = AllocStringArray(BuildEnvironment(info.Environment));
            allocated.Add(envp);
            allocated.AddRange(envStrings);

            var initError = posix_spawn_file_actions_init(out var actions);
                if (initError != 0)
                    throw new Win32Exception(initError, "posix_spawn_file_actions_init failed");
            try
            {
                ThrowIfSpawnActionError(posix_spawn_file_actions_adddup2(ref actions, slave, 0), "posix_spawn_file_actions_adddup2(stdin)");
                ThrowIfSpawnActionError(posix_spawn_file_actions_adddup2(ref actions, slave, 1), "posix_spawn_file_actions_adddup2(stdout)");
                ThrowIfSpawnActionError(posix_spawn_file_actions_adddup2(ref actions, slave, 2), "posix_spawn_file_actions_adddup2(stderr)");

                if (info.WorkingDirectory is { } workingDirectory)
                {
                    var cwd = AllocAnsi(workingDirectory);
                    allocated.Add(cwd);
                    ThrowIfSpawnActionError(
                        posix_spawn_file_actions_addchdir_np(ref actions, cwd),
                        "posix_spawn_file_actions_addchdir_np");
                }

                var spawnError = posix_spawnp(out var pid, file, ref actions, IntPtr.Zero, argv, envp);
                if (spawnError != 0)
                    throw new Win32Exception(spawnError, "posix_spawnp failed");

                return (master, pid);
            }
            finally
            {
                posix_spawn_file_actions_destroy(ref actions);
            }
        }
        finally
        {
            close(slave);
            FreeAll(allocated);
        }
    }

    private static (int Master, int Slave) OpenPty(int rows, int cols)
    {
        var winsize = AllocWinsize(new Winsize
        {
            WsRow = checked((ushort)rows),
            WsCol = checked((ushort)cols),
        });
        var nameBuffer = Marshal.AllocHGlobal(256);
        try
        {
            if (openpty(out var master, out var slave, nameBuffer, IntPtr.Zero, winsize) != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "openpty failed");
            return (master, slave);
        }
        finally
        {
            Marshal.FreeHGlobal(nameBuffer);
            Marshal.FreeHGlobal(winsize);
        }
    }

    private static void ThrowIfSpawnActionError(int error, string operation)
    {
        if (error != 0)
            throw new Win32Exception(error, $"{operation} failed");
    }

    public static void Resize(SafeFileHandle master, int rows, int cols)
    {
        var ws = new Winsize
        {
            WsRow = checked((ushort)rows),
            WsCol = checked((ushort)cols),
        };
        if (ioctl((int)master.DangerousGetHandle(), Tiocswinsz, ref ws) != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ioctl(TIOCSWINSZ) failed");
    }

    public static void Terminate(int pid)
    {
        if (kill(pid, Sigterm) != 0 && Marshal.GetLastWin32Error() != 3)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "kill(SIGTERM) failed");
    }

    public static void Kill(int pid)
    {
        if (kill(pid, Sigkill) != 0 && Marshal.GetLastWin32Error() != 3)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "kill(SIGKILL) failed");
    }

    public static bool TryWait(int pid, out int exitCode)
    {
        var result = waitpid(pid, out var status, Wnohang);
        if (result == pid)
        {
            exitCode = DecodeExitStatus(status);
            return true;
        }

        exitCode = 0;
        return false;
    }

    private static int DecodeExitStatus(int status)
    {
        var signal = status & 0x7f;
        if (signal == 0)
            return (status >> 8) & 0xff;
        return 128 + signal;
    }

    private static IReadOnlyList<string> BuildEnvironment(IReadOnlyDictionary<string, string?>? overrides)
    {
        var env = new Dictionary<string, string?>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            env[(string)entry.Key] = (string?)entry.Value;
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (value is null)
                    env.Remove(key);
                else
                    env[key] = value;
            }
        }

        return env.Select(pair => $"{pair.Key}={pair.Value}").ToList();
    }

    private static IntPtr AllocAnsi(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    private static (IntPtr Array, IntPtr[] Strings) AllocStringArray(IReadOnlyList<string> values)
    {
        var strings = new IntPtr[values.Count];
        for (var index = 0; index < values.Count; index++)
            strings[index] = AllocAnsi(values[index]);

        var array = Marshal.AllocHGlobal(IntPtr.Size * (values.Count + 1));
        Marshal.Copy(strings, 0, array, strings.Length);
        Marshal.WriteIntPtr(array, values.Count * IntPtr.Size, IntPtr.Zero);
        return (array, strings);
    }

    private static IntPtr AllocWinsize(Winsize winsize)
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Winsize>());
        Marshal.StructureToPtr(winsize, pointer, false);
        return pointer;
    }

    private static void FreeAll(IEnumerable<IntPtr> pointers)
    {
        foreach (var pointer in pointers)
        {
            if (pointer != IntPtr.Zero)
                Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort WsRow;
        public ushort WsCol;
        public ushort WsXpixel;
        public ushort WsYpixel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PosixSpawnFileActions
    {
        public int Allocated;
        public int Used;
        public IntPtr Actions;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public int[] Pad;
    }

    [DllImport("libutil", SetLastError = true)]
    private static extern int openpty(out int master, out int slave, IntPtr name, IntPtr termp, IntPtr winp);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int posix_spawnp(out int pid, IntPtr file, ref PosixSpawnFileActions fileActions, IntPtr attrp, IntPtr argv, IntPtr envp);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int posix_spawn_file_actions_init(out PosixSpawnFileActions fileActions);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int posix_spawn_file_actions_destroy(ref PosixSpawnFileActions fileActions);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int posix_spawn_file_actions_adddup2(ref PosixSpawnFileActions fileActions, int fd, int newfd);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addchdir_np(ref PosixSpawnFileActions fileActions, IntPtr path);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref Winsize argp);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);
}
