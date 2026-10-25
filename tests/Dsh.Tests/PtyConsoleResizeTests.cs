using System.Runtime.InteropServices;
using System.Text;
using Dsh.Pty;
using Dsh.ScriptHost;

namespace Dsh.Tests;

public sealed class PtyConsoleResizeTests
{
    internal const string ProbeArgument = "--console-resize-probe";
    private const int Sigwinch = 28;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsoleAttach_ForwardsKernelSizeWithoutResizeSignals(bool redirectOutput)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Unix kernel terminal geometry and SIGWINCH semantics");

        var root = TempTree.CreateSocketDirectory("rz");
        var socketPath = PtyDaemonPaths.SocketPath(root);
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            TempTree.AssertFitsMacOsSocketLimit(socketPath);
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath);
            await daemon.StartAsync(cancellationToken);
            var resident = await PtyDaemonClient.StartAsync(new PtyDaemonStartParams
            {
                FileName = "/bin/sh",
                Arguments = ["-c", "printf 'RESIDENT_READY\\n'; read -r stop"],
                Columns = 100,
                Rows = 30,
            }, socketPath, null, cancellationToken);
            var command = PtcScriptHostClient.ResolveHostCommand()
                ?? throw new InvalidOperationException("The real test executable is unavailable.");
            using var host = new PtyHost();
            var proxy = await host.StartAsync(new PtyStartInfo
            {
                FileName = command.FileName,
                Arguments = [.. command.Arguments.Select(argument => argument == PtcScriptHost.HostArgument ? ProbeArgument : argument), resident.Id, redirectOutput.ToString()],
                Columns = 100,
                Rows = 30,
                Environment = new Dictionary<string, string?> { ["DSH_HOME"] = root },
            }, cancellationToken: cancellationToken);
            Assert.Contains("WARM:100x30", await ReadUntilAsync(proxy, "WARM:100x30"));
            await WaitForSessionAsync(socketPath, resident.Id, session => session.IsAttached);

            proxy.Resize(rows: 24, columns: 80);
            var resized = await WaitForSessionAsync(socketPath, resident.Id, session => session.Columns == 80 && session.Rows == 24);
            Assert.Equal(80, resized.Columns);
            Assert.Equal(24, resized.Rows);

            await PtyDaemonClient.KillAsync(resident.Id, socketPath, null, cancellationToken);
            Assert.Contains("CACHED:100x30", await ReadUntilAsync(proxy, "CACHED:100x30"));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    internal static int RunProbe(string[] arguments)
    {
        var position = Array.IndexOf(arguments, ProbeArgument);
        _ = Console.KeyAvailable; // Complete the runtime's terminal/signal initialization before isolating SIGWINCH.
        var columns = Console.WindowWidth;
        var rows = Console.WindowHeight;
        if (signal(Sigwinch, (nint)1) == (nint)(-1))
            throw new InvalidOperationException("Could not isolate the probe from SIGWINCH.");
        if (bool.Parse(arguments[position + 2]))
        {
            var descriptor = open("/dev/null", 1);
            if (descriptor < 0)
                throw new InvalidOperationException("Could not open the output sink.");
            try
            {
                if (dup2(descriptor, 1) != 1)
                    throw new InvalidOperationException("Could not redirect probe stdout.");
            }
            finally
            {
                close(descriptor);
            }
        }
        using var output = Console.OpenStandardError();
        output.Write(Encoding.ASCII.GetBytes($"WARM:{columns}x{rows}\n"));
        output.Flush();
        PtyDaemonClient.AttachConsoleAsync(arguments[position + 1], output).GetAwaiter().GetResult();
        output.Write(Encoding.ASCII.GetBytes($"CACHED:{Console.WindowWidth}x{Console.WindowHeight}\n"));
        output.Flush();
        return 0;
    }

    private static async Task<string> ReadUntilAsync(PtySession session, string marker)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var count = await session.ReadAsync(buffer, TestContext.Current.CancellationToken);
            if (count == 0)
                break;
            text.Append(Encoding.UTF8.GetString(buffer, 0, count));
        }
        Assert.Contains(marker, text.ToString());
        return text.ToString();
    }

    private static async Task<PtyDaemonSessionDto> WaitForSessionAsync(string socketPath, string id, Func<PtyDaemonSessionDto, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        PtyDaemonSessionDto? current = null;
        do
        {
            current = Assert.Single(await PtyDaemonClient.ListAsync(socketPath, null, TestContext.Current.CancellationToken), session => session.Id == id);
            if (condition(current))
                return current;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        } while (DateTime.UtcNow < deadline);
        Assert.True(condition(current), $"Expected client size/attachment was not observed; resident is {current.Columns}x{current.Rows}, attached={current.IsAttached}.");
        return current;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint signal(int number, nint handler);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldDescriptor, int newDescriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}
