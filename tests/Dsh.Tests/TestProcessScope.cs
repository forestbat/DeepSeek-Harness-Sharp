using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit.v3;

[assembly: Dsh.Tests.TestProcessScope]

namespace Dsh.Tests;

[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class TestProcessScopeAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
        => TestProcessScope.Begin(test.UniqueID);

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
        => TestProcessScope.End(test.UniqueID);
}

internal static class TestProcessScope
{
    private static readonly ConcurrentDictionary<string, Scope> Scopes = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<Scope?> Ambient = new();

    static TestProcessScope()
        => AppDomain.CurrentDomain.ProcessExit += (_, _) => EndAll();

    public static void Begin(string testId)
    {
        var scope = new Scope(testId);
        Scopes[testId] = scope;
        Ambient.Value = scope;
    }

    public static void End(string testId)
    {
        if (Scopes.TryRemove(testId, out var scope))
            scope.KillAll();
        if (Ambient.Value?.TestId == testId)
            Ambient.Value = null;
    }

    public static void EndAll()
    {
        foreach (var scope in Scopes.Values)
            scope.KillAll();
        Scopes.Clear();
        Ambient.Value = null;
    }

    public static Process Start(ProcessStartInfo startInfo)
    {
        EnsureDotnetRoot(startInfo);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"failed to start {startInfo.FileName}");
        Track(process);
        return process;
    }

    private static void EnsureDotnetRoot(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsWindows() || startInfo.UseShellExecute || startInfo.Environment.ContainsKey("DOTNET_ROOT"))
            return;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        if (Directory.Exists(Path.Combine(root, "shared", "Microsoft.AspNetCore.App")))
            startInfo.Environment["DOTNET_ROOT"] = root;
    }

    public static Process Start(string fileName, string arguments)
        => Start(new ProcessStartInfo(fileName, arguments));

    public static Process Start(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return Start(startInfo);
    }

    public static Process Start(string fileName)
        => Start(new ProcessStartInfo(fileName));

    public static void Track(Process process)
    {
        var scope = Ambient.Value ?? FindCurrent();
        scope?.Add(process);
    }

    private static Scope? FindCurrent()
    {
        var testId = TestContext.Current.Test?.UniqueID ?? TestContext.Current.TestCase?.UniqueID;
        return testId is not null && Scopes.TryGetValue(testId, out var scope) ? scope : null;
    }

    private sealed class Scope(string testId)
    {
        private readonly ConcurrentDictionary<int, Process> _processes = new();
        private readonly nint _job = OperatingSystem.IsWindows() ? TestProcessJob.Create() : 0;

        public string TestId => testId;

        public void Add(Process process)
        {
            try
            {
                _processes[process.Id] = process;
                TestProcessJob.Assign(_job, process);
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void KillAll()
        {
            TestProcessJob.Kill(_job);
            foreach (var process in _processes.Values)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                try
                {
                    process.WaitForExit(TimeSpan.FromSeconds(2));
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
            _processes.Clear();
        }
    }
}

internal static class TestProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    public static nint Create()
    {
        var job = CreateJobObject(0, null);
        if (job == 0)
            return 0;
        var info = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JobObjectExtendedLimitInformationData>()))
        {
            CloseHandle(job);
            return 0;
        }
        return job;
    }

    public static void Assign(nint job, Process process)
    {
        if (job == 0)
            return;
        try
        {
            _ = AssignProcessToJobObject(job, process.Handle);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public static void Kill(nint job)
    {
        if (job != 0)
            CloseHandle(job);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformationData info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
