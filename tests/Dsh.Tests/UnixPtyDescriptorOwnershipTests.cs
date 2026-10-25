using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Dsh.Pty;
using Dsh.ScriptHost;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Tests;

// Descriptor-table assertions need the existing process-isolated collection; the spawn test explicitly starts its two PTYs concurrently.
[Collection(SerialProcessCollection.Name)]
public sealed class UnixPtyDescriptorOwnershipTests
{
    private static readonly Type Native = typeof(PtySession).Assembly.GetType("Dsh.Pty.UnixPtyNative")!;
    private const BindingFlags NativeMembers = BindingFlags.Static | BindingFlags.NonPublic;
    private const int FGetFd = 1;
    private const short PollIn = 0x1;
    private const short PollHup = 0x10;
    private const int Ebadf = 9;
    private const int Enoent = 2;
    private const int InvalidDescriptor = int.MaxValue;
    private const int SyscallFailure = -1;
    private const int HangupWaitMilliseconds = 1000;
    internal const string SpawnFailureProbeArgument = "--unix-pty-spawn-failure-probe";

    [Fact(Timeout = 5000)]
    public async Task ConcurrentSpawns_DoNotInheritSlaves_AndOriginalSlaveClosesIndependently()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Unix PTY 描述符语义仅适用于 Linux/macOS");

        var held = OpenPty();
        using var master = new SafeFileHandle((IntPtr)held.Master, ownsHandle: true);
        using var slave = new SafeFileHandle((IntPtr)held.Slave, ownsHandle: true);
        using var firstHost = new PtyHost();
        using var secondHost = new PtyHost();
        var cancellationToken = TestContext.Current.CancellationToken;
        var script = $"""
            printf 'OWN:'; tty
            printf 'READY\n'
            read -r peer
            leaked=0
            for descriptor in /dev/fd/*; do
                if [ "$descriptor" -ef "$peer" ]; then leaked=1; fi
            done
            if [ -e /dev/fd/{held.Master} ] || [ -e /dev/fd/{held.Slave} ]; then leaked=1; fi
            if [ "$leaked" -eq 0 ]; then echo FD-ISOLATED; else echo FD-LEAKED; fi
            echo FD-CHECK-DONE
            read -r finish
            """;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStart = Task.Run(async () =>
        {
            await start.Task;
            return await firstHost.StartAsync(new PtyStartInfo { FileName = "/bin/sh", Arguments = ["-c", script] }, cancellationToken: cancellationToken);
        }, cancellationToken);
        var secondStart = Task.Run(async () =>
        {
            await start.Task;
            return await secondHost.StartAsync(new PtyStartInfo { FileName = "/bin/sh", Arguments = ["-c", script] }, cancellationToken: cancellationToken);
        }, cancellationToken);
        start.SetResult();
        var sessions = await Task.WhenAll(firstStart, secondStart);
        var ready = await Task.WhenAll(ReadUntil(sessions[0], "READY"), ReadUntil(sessions[1], "READY"));
        var names = ready.Select(output => output.Split('\n').Single(line => line.StartsWith("OWN:", StringComparison.Ordinal))[4..].TrimEnd('\r')).ToArray();
        Assert.All(names, name => Assert.StartsWith("/dev/", name));
        Assert.NotEqual(names[0], names[1]);
        await sessions[0].WriteAsync(Encoding.UTF8.GetBytes($"""{names[1]}{Environment.NewLine}"""), cancellationToken);
        await sessions[1].WriteAsync(Encoding.UTF8.GetBytes($"""{names[0]}{Environment.NewLine}"""), cancellationToken);
        var reports = await Task.WhenAll(ReadUntil(sessions[0], "FD-CHECK-DONE"), ReadUntil(sessions[1], "FD-CHECK-DONE"));
        Assert.All(reports, report =>
        {
            Assert.Contains("FD-ISOLATED", report);
            Assert.DoesNotContain("FD-LEAKED", report);
        });

        using (var process = TestProcessScope.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", $"""if [ -e /dev/fd/{held.Master} ] || [ -e /dev/fd/{held.Slave} ]; then echo FD-LEAKED; else echo FD-ISOLATED; fi""" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!)
        {
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("FD-ISOLATED", output.Trim());
        }

        // Both unrelated PTYs remain alive. Their existence cannot keep the original master's stream open.
        slave.Dispose();
        var descriptor = new PollDescriptor { FileDescriptor = held.Master, Events = PollIn };
        Assert.Equal(1, poll(ref descriptor, 1, HangupWaitMilliseconds));
        Assert.NotEqual(0, descriptor.ReturnedEvents & PollHup); // No slave owner remains.
        Assert.All(sessions, session => Assert.Equal(PtySessionStatus.Running, session.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MarkingFailure_ClosesTheOwnedDescriptor(bool invalidMaster)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Unix PTY 描述符语义仅适用于 Linux/macOS");

        using var file = File.Open("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var descriptor = (int)file.SafeFileHandle.DangerousGetHandle();
        var prepare = Native.GetMethod("PreparePty", NativeMembers)!;
        // Transfer the real descriptor to the production ownership boundary. The other descriptor is genuinely invalid.
        file.SafeFileHandle.SetHandleAsInvalid();
        try
        {
            var exception = Assert.Throws<TargetInvocationException>(() => prepare.Invoke(null,
                [invalidMaster ? InvalidDescriptor : descriptor, invalidMaster ? descriptor : InvalidDescriptor, IntPtr.Zero]));
            var nativeError = Assert.IsType<Win32Exception>(exception.InnerException);
            Assert.Equal(Ebadf, nativeError.NativeErrorCode);
            Assert.Equal(SyscallFailure, fcntl(descriptor, FGetFd)); // The valid descriptor was closed on either failure path.
        }
        finally
        {
            if (fcntl(descriptor, FGetFd) != SyscallFailure)
                close(descriptor);
        }
    }

    [Fact]
    public async Task SpawnFailure_ClosesBothDescriptors()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Unix PTY 描述符语义仅适用于 Linux/macOS");

        var command = PtcScriptHostClient.ResolveHostCommand()
            ?? throw new InvalidOperationException("The real test executable is unavailable.");
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(argument == PtcScriptHost.HostArgument ? SpawnFailureProbeArgument : argument);
        using var process = TestProcessScope.Start(startInfo)
            ?? throw new InvalidOperationException("The native descriptor probe did not start.");
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await output;
            var stderr = await errors;
            Assert.True(process.ExitCode == 0, $"""
                Native descriptor cleanup probe exited with {process.ExitCode}.
                {stdout}
                {stderr}
                """);
            Assert.Contains("UNIX_PTY_SPAWN_FAILURE_CLEAN", stdout);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    internal static int RunSpawnFailureProbe()
    {
        try
        {
            var warm = OpenPty();
            using (new SafeFileHandle((IntPtr)warm.Master, ownsHandle: true))
            using (new SafeFileHandle((IntPtr)warm.Slave, ownsHandle: true))
            {
            }
            var directory = OperatingSystem.IsMacOS() ? "/dev/fd" : "/proc/self/fd";
            var spawn = Native.GetMethod("Spawn", BindingFlags.Static | BindingFlags.Public)!;
            var command = new PtyStartInfo { FileName = $"""/dsh-nonexistent-{Guid.NewGuid():N}""", Arguments = [] };
            void FailSpawn()
            {
                var exception = Assert.Throws<TargetInvocationException>(() => spawn.Invoke(null, [command]));
                Assert.Equal(Enoent, Assert.IsType<Win32Exception>(exception.InnerException).NativeErrorCode);
            }

            // This entry runs before xUnit discovery: no previous PTY read, Harness, model or daemon can finish in the background.
            // Warm the real reflection/native error path, directory enumeration, assertions and standard streams before measuring.
            FailSpawn();
            _ = Directory.GetFileSystemEntries(directory).ToHashSet(StringComparer.Ordinal);
            Console.Out.Flush();
            Console.Error.Flush();
            var before = Directory.GetFileSystemEntries(directory).ToHashSet(StringComparer.Ordinal);
            FailSpawn();
            var after = Directory.GetFileSystemEntries(directory).ToHashSet(StringComparer.Ordinal);
            Assert.Empty(after.Except(before, StringComparer.Ordinal));
            Assert.Empty(before.Except(after, StringComparer.Ordinal));
            Console.WriteLine("UNIX_PTY_SPAWN_FAILURE_CLEAN");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static (int Master, int Slave, string SlaveName) OpenPty()
    {
        // Use the same gate as the production allocation path, rather than opening an unprotected test-only race window.
        using var allocation = ((Lock)Native.GetField("SpawnGate", NativeMembers)!.GetValue(null)!).EnterScope();
        return ((int, int, string))Native.GetMethod("OpenPty", NativeMembers)!.Invoke(null, [20, 80])!;
    }

    private static async Task<string> ReadUntil(PtySession session, string marker)
    {
        var output = new StringBuilder();
        var buffer = new byte[4096];
        while (!output.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var read = await session.ReadAsync(buffer, TestContext.Current.CancellationToken);
            if (read == 0)
                break;
            output.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        Assert.Contains(marker, output.ToString());
        return output.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollDescriptor descriptors, nuint count, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int descriptor, int command);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}
