using System.Text;
using Dsh.Pty;

namespace Dsh.Tests;

/** 临时诊断: 探明 WSL/Linux 上 harness→proxy→daemon→常驻 TUI 的按键到底走到哪一步。用完删除。 */
[Collection(SerialProcessCollection.Name)]
public sealed class ZzProbeTests
{
    [Fact]
    public async Task Probe_Key_Delivery()
    {
        using var harness = await PtyTuiHarness.StartAsync();
        if (harness is null)
            return;
        var ct = TestContext.Current.CancellationToken;
        var report = new StringBuilder();
        try
        {
            await harness.WaitForAsync("快捷键", TimeSpan.FromSeconds(60));
            var id = await FindSessionAsync(harness.Home, ct);
            report.AppendLine($"session: {id}");
            report.AppendLine($"proxy pid: {harness.Session.ProcessId}");

            report.AppendLine("--- proxy termios (before) ---");
            report.AppendLine(Stty(harness.Session.ProcessId));

            await harness.WriteTextAsync("DRAFTX");
            await Task.Delay(1500, ct);
            report.AppendLine("--- after DRAFTX ---");
            report.AppendLine(InputRow(await SnapshotTextAsync(id, ct)));

            report.AppendLine("--- force stty raw on proxy fd0 ---");
            report.AppendLine(Run("stty", $"raw -F /proc/{harness.Session.ProcessId}/fd/0"));
            report.AppendLine(Stty(harness.Session.ProcessId));

            await harness.WriteTextAsync("DRAFTY");
            await Task.Delay(1500, ct);
            report.AppendLine("--- after DRAFTY (post stty raw) ---");
            report.AppendLine(InputRow(await SnapshotTextAsync(id, ct)));

            await harness.WriteBytesAsync(0x18);
            await Task.Delay(1500, ct);
            report.AppendLine("--- after 0x18 (post stty raw) ---");
            report.AppendLine(InputRow(await SnapshotTextAsync(id, ct)));
        }
        finally
        {
            Directory.CreateDirectory(ArtifactsDirectory());
            File.WriteAllText(Path.Combine(ArtifactsDirectory(), "zz-probe.txt"), report.ToString());
            harness.SaveTranscript();
            harness.Dispose();
        }
    }

    private static string InputRow(string screen)
    {
        foreach (var line in screen.Split('\n'))
        {
            if (line.Contains("ready", StringComparison.Ordinal) || line.TrimStart().StartsWith('>'))
                return line.TrimEnd();
        }

        return screen;
    }

    private static string Stty(int? pid)
    {
        if (pid is null or <= 0)
            return "(no pid)";
        return Run("stty", $"-a -F /proc/{pid}/fd/0");
    }

    private static string Run(string fileName, string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return "(failed to start)";
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private static async Task<string> FindSessionAsync(string home, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var sessions = await PtyDaemonClient.ListAsync(cancellationToken: ct);
            var match = sessions.FirstOrDefault(session => session.Command.Contains(home, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match.Id;
            await Task.Delay(200, ct);
        }

        return "";
    }

    /** attach 一次, 2 秒后取消: 捕获服务端下发的状态重建快照(即常驻会话"现在长什么样")。 */
    private static async Task<string> SnapshotTextAsync(string id, CancellationToken ct)
    {
        var output = new MemoryStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await PtyDaemonClient.AttachAsync(id, new HangStream(), output, cts.Token);
        }
        catch (Exception)
        {
        }

        var screen = new VtScreen(PtyTuiHarness.Columns, PtyTuiHarness.Rows);
        screen.Feed(output.ToArray());
        var builder = new StringBuilder();
        for (var y = 0; y < screen.Height; y++)
        {
            foreach (var cell in screen.Row(y))
                builder.Append(cell.Character == '\0' ? ' ' : cell.Character);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string ArtifactsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "artifacts", "debug-screenshots");
    }

    private sealed class HangStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set { }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => 0;

        public override void SetLength(long value)
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
