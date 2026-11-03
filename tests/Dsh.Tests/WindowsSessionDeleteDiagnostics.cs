using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Tests;

[SupportedOSPlatform("windows")]
internal static class WindowsSessionDeleteDiagnostics
{
    private const int MaximumResources = 16;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    public static string Capture(string directory)
    {
        var files = Directory.EnumerateFiles(directory).Take(MaximumResources + 1).ToArray();
        var resources = files.Take(MaximumResources).ToArray();
        var probes = resources.Select((path, index) => $"{index}:{ProbeDeleteAccess(path)}:{(int)File.GetAttributes(path)}");
        return $"directory-delete-access={ProbeDeleteAccess(directory)} directory-attributes={(int)File.GetAttributes(directory)} resources={resources.Length} resources-truncated={files.Length > MaximumResources} file-delete-access-and-attributes=[{string.Join(",", probes)}] {ResourceOwners(resources)}";
    }

    private static int ProbeDeleteAccess(string path)
    {
        using var handle = CreateFile(path, DeleteAccess, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        return handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
    }

    private static string ResourceOwners(string[] resources)
    {
        var status = RmStartSession(out var session, 0, new StringBuilder(33));
        if (status != 0)
            return $"restart-manager-start={status}";
        try
        {
            status = RmRegisterResources(session, (uint)resources.Length, resources, 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (status != 0)
                return $"restart-manager-register={status}";
            var processes = new ProcessInfo[MaximumResources];
            uint count = (uint)processes.Length;
            uint reasons = 0;
            status = RmGetList(session, out var required, ref count, processes, ref reasons);
            if (status != 0)
                return $"restart-manager-list={status} owners-required={required}";
            var owners = processes.Take((int)count).Select(process =>
                $"{process.Process.ProcessId}:{(process.Process.ProcessId == Environment.ProcessId ? "current" : "external")}");
            return $"restart-manager-list=0 owners=[{string.Join(",", owners)}]";
        }
        finally
        {
            RmEndSession(session);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint ApplicationType;
        public uint ApplicationStatus;
        public uint TerminalSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint creationDisposition, uint flags, IntPtr template);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmRegisterResources(uint session, uint fileCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] files,
        uint applicationCount, IntPtr applications, uint serviceCount, IntPtr services);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmGetList(uint session, out uint required, ref uint count,
        [In, Out] ProcessInfo[] processes, ref uint rebootReasons);

    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmEndSession(uint session);
}
