using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

[SupportedOSPlatform("windows")]
internal static class WindowsNative
{
    internal static readonly string[] NetworkCapabilities = ["S-1-15-3-1", "S-1-15-3-2", "S-1-15-3-3"];
    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityCapabilities { public nint Sid, Capabilities; public uint CapabilityCount, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes { public nint Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct GenericMapping { public uint Read, Write, Execute, All; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct JobBasicLimit
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct JobExtendedLimit
    {
        public JobBasicLimit Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        public uint TotalPageFaults, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int CreateAppContainerProfile(string name, string displayName, string description, nint capabilities, uint count, out nint sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] internal static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] internal static extern nint FreeSid(nint sid);
    [DllImport("kernel32.dll")] internal static extern nint LocalFree(nint value);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ConvertStringSidToSid(string text, out nint sid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, nint information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out JobAccounting information, uint size, nint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
    [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string executable, StringBuilder commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string workingDirectory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(SafeFileHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, int level, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AccessCheck(nint descriptor, SafeAccessTokenHandle token, uint access,
        ref GenericMapping mapping, nint privileges, ref uint privilegesLength, out uint granted, [MarshalAs(UnmanagedType.Bool)] out bool allowed);
    [DllImport("advapi32.dll")]
    internal static extern void MapGenericMask(ref uint access, ref GenericMapping mapping);
    internal static SafeFileHandle CreateFile(string path, uint access, uint share, nint attributes, uint disposition, uint flags, nint template)
        => CreateFileNative(PathForNative(path), access, share, attributes, disposition, flags, template);
    internal static string PathForNative(string path) => path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path : $"\\\\?\\{path}";
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(string path, uint access, uint share, nint attributes, uint disposition, uint flags, nint template);

    internal static unsafe SafeFileHandle OpenReadAcl(string path)
    {
        const uint readControl = 0x00020000, readAttributes = 0x00000080, synchronize = 0x00100000;
        const uint caseInsensitive = 0x40, synchronousIoNonAlert = 0x20, backupIntent = 0x4000;
        const uint shareAll = (uint)(FileShare.Read | FileShare.Write | FileShare.Delete);
        const int stackCharacters = 512, errorFilenameExceedsRange = 206;
        // Policy paths are canonical local absolute paths; replace the extended DOS prefix without normalizing the suffix.
        var source = path.AsSpan();
        if (source.StartsWith(@"\\?\", StringComparison.Ordinal))
            source = source[4..];
        const string ntPrefix = @"\??\";
        var length = source.Length + ntPrefix.Length;
        // Keep room for the terminator required by the former Win32 path conversion, without overflowing UNICODE_STRING.
        if (length > ushort.MaxValue / sizeof(char) - 1)
            return FailedFileHandle(errorFilenameExceedsRange);
        char[]? rented = null;
        Span<char> buffer = length <= stackCharacters ? stackalloc char[length]
            : rented = ArrayPool<char>.Shared.Rent(length);
        SafeFileHandle handle;
        int error;
        try
        {
            ntPrefix.AsSpan().CopyTo(buffer);
            source.CopyTo(buffer[ntPrefix.Length..]);
            fixed (char* text = buffer)
            {
                var name = new UnicodeString
                {
                    Length = (ushort)(length * sizeof(char)),
                    MaximumLength = (ushort)(length * sizeof(char)),
                    Buffer = text
                };
                var attributes = new ObjectAttributes
                {
                    Length = (uint)sizeof(ObjectAttributes),
                    ObjectName = &name,
                    Attributes = caseInsensitive
                };
                // CreateFileW implicitly adds FILE_READ_ATTRIBUTES and SYNCHRONIZE even for READ_CONTROL-only callers.
                var status = NtOpenFile(out handle, readControl | readAttributes | synchronize, ref attributes,
                    out _, shareAll, synchronousIoNonAlert | backupIntent);
                error = status < 0 ? (int)RtlNtStatusToDosError(status) : 0;
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
        if (error == 0)
            return handle;
        handle.Dispose();
        return FailedFileHandle(error);
    }

    private static SafeFileHandle FailedFileHandle(int error)
    {
        var handle = new SafeFileHandle(-1, ownsHandle: true);
        // Restore the captured error only after all buffer and native-handle cleanup.
        Marshal.SetLastPInvokeError(error);
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtOpenFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock statusBlock, uint share, uint options);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint information, nint owner, nint group, nint dacl, nint sacl);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out nint converted, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetSecurityDescriptorSacl(nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out nint sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetSecurityDescriptorControl(nint descriptor, out ushort control, out uint revision);
    [DllImport("advapi32.dll")]
    internal static extern uint GetSecurityDescriptorLength(nint descriptor);
    [DllImport("ntdll.dll")]
    internal static extern int RtlGetLastNtStatus();
}
