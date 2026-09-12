using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Pty;

internal sealed class ConPtySession : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint Infinite = 0xFFFFFFFF;

    private readonly IntPtr _pseudoConsole;
    private readonly IntPtr _processHandle;
    private readonly IntPtr _threadHandle;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private bool _disposed;

    private ConPtySession(IntPtr pseudoConsole, IntPtr processHandle, IntPtr threadHandle, int pid, FileStream input, FileStream output)
    {
        _pseudoConsole = pseudoConsole;
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        Pid = pid;
        _input = input;
        _output = output;
        Task.Run(MonitorExit);
    }

    public event Action<int?>? Exited;

    public int Pid { get; }

    public Stream Stream => _output;

    public FileStream Input => _input;

    public static ConPtySession? TryStart(PtyStartInfo info)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return Start(info);
        }
        catch
        {
            return null;
        }
    }

    internal static ConPtySession Start(PtyStartInfo info)
    {
        var security = new SecurityAttributes { InheritHandle = true };
        if (!CreatePipe(out var inputRead, out var inputWrite, security))
            throw new InvalidOperationException($"CreatePipe failed: {Marshal.GetLastWin32Error()}");
        if (!CreatePipe(out var outputRead, out var outputWrite, security))
            throw new InvalidOperationException($"CreatePipe failed: {Marshal.GetLastWin32Error()}");

        var size = new Coord((short)Math.Max(1, info.Columns), (short)Math.Max(1, info.Rows));
        var hr = CreatePseudoConsole(size, inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), 0, out var pseudoConsole);
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0)
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

        var attributeSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
        var attributeList = Marshal.AllocHGlobal(attributeSize);
        var environment = IntPtr.Zero;
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
                throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");

            var startupInfo = new StartupInfoEx();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            startupInfo.lpAttributeList = attributeList;

            var flags = ExtendedStartupInfoPresent;
            if (info.Environment is not null)
            {
                flags |= CreateUnicodeEnvironment;
                environment = BuildEnvironmentBlock(info.Environment);
            }

            var commandLine = BuildCommandLine(info);
            var (savedInput, savedOutput, savedError) = ParkStdHandles();
            ProcessInformation processInfo;
            try
            {
                if (!CreateProcess(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        flags,
                        environment,
                        info.WorkingDirectory,
                        ref startupInfo,
                        out processInfo))
                    throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                RestoreStdHandles(savedInput, savedOutput, savedError);
            }

            var input = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false);
            var output = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false);
            return new ConPtySession(pseudoConsole, processInfo.hProcess, processInfo.hThread, processInfo.dwProcessId, input, output);
        }
        finally
        {
            if (environment != IntPtr.Zero)
                Marshal.FreeHGlobal(environment);
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    public void Resize(int rows, int columns)
    {
        if (_disposed)
            return;
        ResizePseudoConsole(_pseudoConsole, new Coord((short)Math.Max(1, columns), (short)Math.Max(1, rows)));
    }

    public async Task StopAsync()
    {
        if (_disposed)
            return;
        try
        {
            TerminateProcess(_processHandle, 0);
            await Task.Run(() => WaitForSingleObject(_processHandle, 5000));
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ClosePseudoConsole(_pseudoConsole);
        _input.Dispose();
        _output.Dispose();
        if (_threadHandle != IntPtr.Zero)
            CloseHandle(_threadHandle);
        if (_processHandle != IntPtr.Zero)
            CloseHandle(_processHandle);
    }

    private async Task MonitorExit()
    {
        await Task.Run(() => WaitForSingleObject(_processHandle, Infinite));
        int? exitCode = null;
        if (GetExitCodeProcess(_processHandle, out var code))
            exitCode = (int)code;
        Exited?.Invoke(exitCode);
    }

    private static string BuildCommandLine(PtyStartInfo info)
    {
        var builder = new StringBuilder();
        AppendQuoted(builder, info.FileName);
        foreach (var argument in info.Arguments)
        {
            builder.Append(' ');
            AppendQuoted(builder, argument);
        }
        return builder.ToString();
    }

    private static void AppendQuoted(StringBuilder builder, string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"'))
        {
            builder.Append(value);
            return;
        }
        builder.Append('"');
        builder.Append(value.Replace("\\\"", "\\\\\"", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal));
        builder.Append('"');
    }

    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> environment)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            merged[(string)entry.Key] = (string?)entry.Value;
        foreach (var (key, value) in environment)
            merged[key] = value;
        var builder = new StringBuilder();
        foreach (var (key, value) in merged)
        {
            if (value is null)
                continue;
            builder.Append(key).Append('=').Append(value).Append('\0');
        }
        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord(short x, short y)
    {
        public short X = x;
        public short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class SecurityAttributes
    {
        public int Length = Marshal.SizeOf<SecurityAttributes>();
        public IntPtr SecurityDescriptor = IntPtr.Zero;
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private static (IntPtr Input, IntPtr Output, IntPtr Error) ParkStdHandles()
    {
        var saved = (GetStdHandle(StdInputHandle), GetStdHandle(StdOutputHandle), GetStdHandle(StdErrorHandle));
        SetStdHandle(StdInputHandle, IntPtr.Zero);
        SetStdHandle(StdOutputHandle, IntPtr.Zero);
        SetStdHandle(StdErrorHandle, IntPtr.Zero);
        return saved;
    }

    private static void RestoreStdHandles(IntPtr input, IntPtr output, IntPtr error)
    {
        SetStdHandle(StdInputHandle, input);
        SetStdHandle(StdOutputHandle, output);
        SetStdHandle(StdErrorHandle, error);
    }

    private const uint StdInputHandle = 0xFFFFFFF6;
    private const uint StdOutputHandle = 0xFFFFFFF5;
    private const uint StdErrorHandle = 0xFFFFFFF4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(uint nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(uint nStdHandle, IntPtr hHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, SecurityAttributes lpPipeAttributes, uint nSize = 0);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref StartupInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
