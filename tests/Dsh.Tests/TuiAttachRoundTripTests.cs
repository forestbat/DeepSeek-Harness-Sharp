using System.Text;

namespace Dsh.Tests;

/**
 * attach 端到端真流程验收(复用现有基建: PtyTuiHarness 起真实 TUI, VtScreen 当 attach 侧终端, TuiScreenCaptureTests.SavePng 出图):
 *   真 TUI 里 `/detach` → 当前会话交给 daemon 里的新 TUI 继续跑, 本 TUI 退出 → 从 daemon 侧取新增 id
 *   → 在**另一个真实终端**(PTY 子进程就是 CLI 本身, 无 shell 中继)里跑 `dsh tui attach ID`
 *   → 把 attach 输出喂进一个全新 VT 屏 → 断言画面上必须出现完整 TUI 的侧栏/输入行/状态行, 并落 PNG 供人眼确认。
 * 归入 GUI 串行集合: 本用例要起真 TUI/daemon, 与并行的时序敏感用例抢资源会造成抖动。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class TuiAttachRoundTripTests
{
    [Fact]
    public async Task Attach_After_Detach_Restores_The_Session_Screen()
    {
        // 不 using: 被 detach 的 TUI 可能仍占着隔离 home 里的日志文件, 夹具 Dispose 删目录会抛 IOException 而让用例假失败。
        var tui = await PtyTuiHarness.StartAsync();
        if (tui is null)
            return;   // 环境不具备(缺 settings.yaml / AF_UNIX 不可用)时, 与其它 PTY-TUI 用例一致地跳过

        var cancellationToken = TestContext.Current.CancellationToken;
        await tui.WaitForAsync("快捷键", TimeSpan.FromSeconds(60));
        await EnsureDaemonAsync(cancellationToken);
        var before = (await PtyDaemonClient.ListAsync(cancellationToken: cancellationToken))
            .Select(session => session.Id)
            .ToHashSet(StringComparer.Ordinal);

        // ① 真 TUI 里 detach(缺省): 当前 TUI 会话交给 daemon 继续跑, 本 TUI 退出
        await tui.WriteTextAsync("/detach");
        await tui.WriteBytesAsync(0x0D);
        var id = await WaitForNewSessionAsync(before, TimeSpan.FromSeconds(40), cancellationToken);
        if (id.Length == 0)
        {
            tui.SaveTranscript();
            Assert.Fail($"detach 后 daemon 里没出现新会话; 当前画面:\n{tui.Snapshot()}");
            return;
        }

        // ② 另一个真实终端: PTY 子进程直接是 CLI 的 attach(没有 shell 中继, 字节直通)
        using var host = new PtyHost();
        // 显式释放而不是 using: 泵任务要读到会话停止之后, using 的"离开作用域即释放"会在泵还在读时就把会话关掉。
        var terminal = await host.StartAsync(
            new PtyStartInfo
            {
                FileName = TuiExecutable(),
                Arguments = ["tui", "attach", id],
                Rows = PtyTuiHarness.Rows,
                Columns = PtyTuiHarness.Columns,
            },
            cancellationToken: cancellationToken);

        var screen = new VtScreen(PtyTuiHarness.Columns, PtyTuiHarness.Rows);
        var raw = new MemoryStream();
        var collector = Task.Run(() => PumpAsync(terminal, screen, raw), cancellationToken);
        await WaitForScreenAsync(screen, "session:", TimeSpan.FromSeconds(25), cancellationToken);

        // ③ 断言: attach 回来必须是**完整 TUI 画面**(侧栏 + 输入行 + 状态行), 而不是空屏或一行提示符
        SaveScreenshot(screen, raw.ToArray(), id);
        var rendered = Rendered(screen);
        Assert.Contains("session:", rendered, StringComparison.Ordinal);   // 侧栏内容
        Assert.Contains("│", rendered, StringComparison.Ordinal);          // 侧栏边框
        Assert.Contains("Enter", rendered, StringComparison.Ordinal);      // 输入行提示
        Assert.Contains("Ctrl+C", rendered, StringComparison.Ordinal);     // 状态行/快捷键
        Assert.Contains("上", rendered, StringComparison.Ordinal);         // 中文按 UTF-8 正确还原
        Assert.Contains("文", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("鈹", rendered, StringComparison.Ordinal);   // 不得是 UTF-8 被按 OEM 代码页误读的乱码

        // ⑤ 收尾: Ctrl+C×2 让会话里的 TUI 退出 —— 否则它继续占着隔离 home 的日志与构建产物。
        // 走产品自己的 AttachAsync(无 ConPTY 中继)而不是往 PTY 里写 0x03: 后者会被 ConPTY 当成控制台信号, 到不了会话。
        await QuitAttachedSessionAsync(id, cancellationToken);
        await WaitForSessionExitAsync(id, TimeSpan.FromSeconds(15), cancellationToken);
        await terminal.StopAsync();
        await collector;
        terminal.Dispose();
        await WaitForLogReleaseAsync(tui.Home, cancellationToken);
        tui.Dispose();
    }

    private static async Task QuitAttachedSessionAsync(string id, CancellationToken cancellationToken)
    {
        for (var press = 0; press < 2; press++)
        {
            using var keys = new MemoryStream([0x03]);
            await PtyDaemonClient.AttachAsync(id, keys, Stream.Null, cancellationToken);
            await Task.Delay(400, cancellationToken);
        }
    }

    /** daemon 托管的 TUI 退出后日志句柄可能还持有片刻: 等它释放再删隔离 home, 免得夹具清理把已通过的用例判成失败。 */
    private static async Task WaitForLogReleaseAsync(string home, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40 && !LogsAreUnlocked(home); attempt++)
            await Task.Delay(250, cancellationToken);
    }

    private static bool LogsAreUnlocked(string home)
    {
        var directory = Path.Combine(home, "logs");
        if (!Directory.Exists(directory))
            return true;
        foreach (var file in Directory.GetFiles(directory, "*.log"))
        {
            try
            {
                using var stream = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task WaitForScreenAsync(VtScreen screen, string marker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken);
            if (Rendered(screen).Contains(marker, StringComparison.Ordinal))
                return;
        }
    }

    private static async Task WaitForSessionExitAsync(string id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken);
            var sessions = await PtyDaemonClient.ListAsync(cancellationToken: cancellationToken);
            var info = sessions.FirstOrDefault(session => session.Id == id);
            if (info is null || !string.Equals(info.Status, "Running", StringComparison.Ordinal))
                return;
        }
    }

    /** 用 CLI 拉起 daemon: 测试进程自己调 EnsureRunningAsync 会去 spawn Dsh.Tests.exe, 起不来。 */
    private static async Task EnsureDaemonAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await PtyDaemonClient.IsRunningAsync(cancellationToken))
                return;
        }
        catch (Exception)
        {
            // 未运行: 走下面的 CLI 拉起
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo(TuiExecutable())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("tui");
        startInfo.ArgumentList.Add("list");
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
            return;
        await process.WaitForExitAsync(cancellationToken);
    }

    private static async Task<string> WaitForNewSessionAsync(
        HashSet<string> before,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken);
            var sessions = await PtyDaemonClient.ListAsync(cancellationToken: cancellationToken);
            var created = sessions.Select(session => session.Id).FirstOrDefault(candidate => !before.Contains(candidate));
            if (created is { Length: > 0 })
                return created;
        }

        return "";
    }

    private static async Task PumpAsync(PtySession session, VtScreen screen, MemoryStream raw)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await session.ReadAsync(buffer, CancellationToken.None);
                if (read <= 0)
                    break;
                raw.Write(buffer, 0, read);
                lock (screen)
                    screen.Feed(buffer.AsSpan(0, read));
            }
        }
        catch (Exception)
        {
            // 会话结束/被停止: 泵退出
        }
    }

    private static string Rendered(VtScreen screen)
    {
        lock (screen)
        {
            var builder = new StringBuilder();
            for (var y = 0; y < screen.Height; y++)
            {
                foreach (var cell in screen.Row(y))
                    builder.Append(cell.Character == '\0' ? ' ' : cell.Character);
                builder.Append('\n');
            }

            return builder.ToString();
        }
    }

    private static void SaveScreenshot(VtScreen screen, byte[] raw, string id)
    {
        var cells = new char[screen.Width, screen.Height];
        lock (screen)
        {
            for (var y = 0; y < screen.Height; y++)
            {
                var row = screen.Row(y);
                for (var x = 0; x < screen.Width; x++)
                    cells[x, y] = row[x].Character == '\0' ? ' ' : row[x].Character;
            }
        }

        var tag = OperatingSystem.IsWindows() ? "windows" : "linux";
        TuiScreenCaptureTests.SavePng(cells, $"tui-attach-restored-{tag}.png");
        Directory.CreateDirectory(ArtifactsDirectory());
        File.WriteAllText(
            Path.Combine(ArtifactsDirectory(), $"tui-attach-restored-{tag}.txt"),
            $"session: {id}\n" + Rendered(screen),
            new UTF8Encoding(false));
        // 原始字节留档: 画面空白时据此区分"客户端没写出"与"快照本身是空的"
        File.WriteAllBytes(Path.Combine(ArtifactsDirectory(), $"tui-attach-direct-{tag}.bin"), raw);
    }

    private static string TuiExecutable()
    {
        var root = FindRepositoryRoot();
        var name = OperatingSystem.IsWindows() ? "DeepSeek-Harness-Sharp.exe" : "DeepSeek-Harness-Sharp";
        return Path.Combine(root, "DeepSeek-Harness-Sharp", "bin", "Debug", "net10.0", name);
    }

    private static string ArtifactsDirectory()
        => Path.Combine(FindRepositoryRoot(), "artifacts", "debug-screenshots");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
