using System.Text;
using Dsh.Boot;
using Dsh.Pty;

namespace Dsh.Tests;

/**
 * 真 TUI 的输入端到端: 测试进程扮演终端/代理, 把**原始终端字节**写进 daemon 会话, 常驻 TUI 从输入旁路拿到它们。
 *
 * 为什么不用 PtyTuiHarness: 那条路要经过一层 ConPTY, 而 ConPTY 会把 `ESC[200~` 粘贴标记与 CSI-u 修饰键翻译成普通按键,
 * 于是"多行粘贴变多条消息""Shift+Enter 变裸回车"在夹具里根本复现不出真实形状。
 * 这里不经过任何 ConPTY 输入翻译, 与用户在真终端里发出的字节完全一致。
 */
public class TuiDaemonInputTests
{
    private const int Columns = 100;
    private const int Rows = 30;

    [Fact(Timeout = 120000)]
    public async Task PastedText_StaysOneDraft_ShiftEnterBreaks_AndCjkPunctuationSurvives()
    {
        var executable = RealHostProcesses.Executable;
        Assert.True(File.Exists(executable), $"缺少 dsharp: {executable}");
        var home = RealHostProcesses.CreateHome("tuiin");
        RealHostProcesses.CopyRealConfiguration(home);
        var run = Path.Combine(home, "run");
        Directory.CreateDirectory(run);
        var socketPath = Path.Combine(run, "dsh.sock");
        var portFile = OperatingSystem.IsWindows() ? Path.Combine(run, "dsh.port") : null;
        try
        {
            await using var daemon = new PtyDaemon(socketPath: socketPath, portFile: portFile);
            await daemon.StartAsync(TestContext.Current.CancellationToken);
            var session = await PtyDaemonClient.StartAsync(
                new PtyDaemonStartParams
                {
                    FileName = executable,
                    Arguments = ["tui"],
                    Rows = Rows,
                    Columns = Columns,
                    Home = home,
                    Environment = new Dictionary<string, string?>
                    {
                        [HarnessHome.HomeEnv] = home,
                        [HarnessHome.ChildHomeEnv] = home,
                        [PtySessionProtocol.ChildVariable] = "1",
                    },
                },
                socketPath,
                portFile,
                TestContext.Current.CancellationToken);

            await using var terminal = await PtyDaemonClient.SubscribeAsync(
                session.Id, socketPath, portFile, TestContext.Current.CancellationToken);
            using var screen = new ScreenReader(terminal);

            await screen.WaitForAsync(text => text.Contains("Enter 发送", StringComparison.Ordinal), TimeSpan.FromSeconds(60));

            // ① 括号粘贴: 整段进草稿(两行都在输入区), 没有被当成回车逐行发出去
            terminal.Send("\u001b[200~第一行\r\n第二行\u001b[201~"u8.ToArray());
            var pasted = await screen.WaitForAsync(
                text => text.Contains("第二行", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20));
            var draft = InputArea(pasted);
            Assert.True(draft.Count >= 2, "粘贴后草稿不成两行:\n" + string.Join('\n', draft) + "\n\n" + pasted);
            Assert.Contains("第一行", draft[0]);
            Assert.Contains("第二行", draft[1]);

            // ② 中文歧义标点(EAW=Ambiguous)原样进入草稿, 不被换成 ASCII 也不丢
            terminal.Send("“引号”……——"u8.ToArray());
            var punctuation = await screen.WaitForAsync(
                text => text.Contains("——", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20));
            var punctuationDraft = InputArea(punctuation);
            Assert.Contains(punctuationDraft, row => row.Contains("“引号”……——", StringComparison.Ordinal));

            // ③ Shift+Enter 的两种终端编码都能换行: 两个字母落在不同行, 且没有发送(草稿还在)
            foreach (var (pair, sequence) in new[] { ("AZ", "\u001b[13;2u"), ("CY", "\u001b[27;2;13~") })
            {
                terminal.Send(Encoding.UTF8.GetBytes(pair[0] + sequence + pair[1]));
                var broken = await screen.WaitForAsync(
                    text => text.Contains(pair[1], StringComparison.Ordinal),
                    TimeSpan.FromSeconds(20));
                Assert.DoesNotContain(pair, broken);   // 同一行相邻 = 没有换行
                Assert.True(InputArea(broken).Any(row => row.Contains(pair[1], StringComparison.Ordinal)), "换行后的字不在输入区:\n" + broken);
            }
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            TempTree.Delete(home);
        }
    }

    /** 输入区: 从带提示符的那行到提示行为止(屏幕行去掉宽字符占位空格)。 */
    private static List<string> InputArea(string screen)
    {
        var rows = screen.Split('\n').Select(row => row.Replace(" ", string.Empty)).ToList();
        var start = rows.FindIndex(row => row.StartsWith('>'));
        if (start < 0)
            return [];
        var end = rows.FindIndex(start, row => row.Contains("Enter发送", StringComparison.Ordinal));
        return end < start ? [rows[start]] : rows.GetRange(start, end - start);
    }

    /** 把会话输出喂给 VtScreen, 按文字条件等待: 屏幕模型与产品用的是同一份解释器。 */
    private sealed class ScreenReader : IDisposable
    {
        private readonly VtScreen _screen = new(Columns, Rows);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _pump;

        public ScreenReader(PtyRemoteSession terminal)
        {
            _pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var chunk in terminal.Output.ReadAllAsync(_stop.Token))
                        lock (_screen)
                            _screen.Feed(chunk);
                }
                catch (Exception)
                {
                }
            });
        }

        public string Text
        {
            get
            {
                lock (_screen)
                {
                    var text = new StringBuilder();
                    for (var y = 0; y < _screen.Height; y++)
                    {
                        var row = _screen.Row(y);
                        var chars = new char[row.Length];
                        for (var x = 0; x < row.Length; x += 1)
                            chars[x] = row[x].Character is '\0' ? ' ' : row[x].Character;
                        text.AppendLine(new string(chars));
                    }
                    return text.ToString();
                }
            }
        }

        public async Task<string> WaitForAsync(Func<string, bool> ready, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var text = Text;
                if (ready(text) || DateTime.UtcNow >= deadline)
                    return text;
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _pump.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }

            _stop.Dispose();
        }
    }
}

