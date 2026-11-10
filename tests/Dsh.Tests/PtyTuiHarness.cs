using System.Net.Sockets;
using System.Text;
using Dsh.Boot;
using Dsh.Tui.Services;

namespace Dsh.Tests;

/**
 * 真实 dsharp TUI 跑在**工程自身 PTY**(PtyHost/PtySession)上的测试夹具:
 * 启动、按键、按标记等待输出、留档、把输出解析成字符屏。不依赖任何终端模拟器或 tmux。
 */
internal sealed class PtyTuiHarness : IDisposable
{
    public const int Columns = 100;
    public const int Rows = 30;

    private static readonly string TranscriptPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts020", "debug-screenshots", "tui-pty-stream.txt"));

    private readonly PtyHost _host;
    private readonly StringBuilder _text = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _pump;
    private int _outputPumpStarted;

    private PtyTuiHarness(PtyHost host, PtySession session, string home)
    {
        _host = host;
        Session = session;
        Home = home;
        _pump = Task.Run(PumpAsync);
    }

    public PtySession Session { get; }

    public string Home { get; }

    public bool OutputPumpStarted => Volatile.Read(ref _outputPumpStarted) != 0;

    public TaskStatus OutputPumpStatus => _pump.Status;

    /** 测试构建必须提供真实 TUI; 环境缺配置/不支持 AF_UNIX 时 Assert.Skip。 */
    public static async Task<PtyTuiHarness> StartAsync(params string[] extraArguments)
    {
        var executable = RealHostProcesses.Executable;
        var realHome = HarnessHome.Default().Root;
        if (!File.Exists(Path.Combine(realHome, "settings.yaml")))
            Assert.Skip($"环境缺少 {realHome}/settings.yaml, 无法启动 dsharp");
        var home = PrepareIsolatedHome();
        if (!SupportsUnixSockets(home))
            Assert.Skip($"{home} 所在文件系统不支持 AF_UNIX(如 WSL 的 /mnt drvfs); 请把仓库放在原生文件系统上运行");

        var host = new PtyHost();
        try
        {
            var arguments = new List<string> { "tui" };
            arguments.AddRange(extraArguments);
            var session = await host.StartAsync(new PtyStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                Rows = Rows,
                Columns = Columns,
                Home = home,
                Environment = new Dictionary<string, string?> { [HarnessHome.ChildHomeEnv] = home },
            }, cancellationToken: TestContext.Current.CancellationToken);
            return new PtyTuiHarness(host, session, home);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /**
     * Run the CLI directly, with the ordinary POSIX shell retaining terminal ownership after /exit.
     * The shell reads once before taking the restored stty snapshot: like a caller's next prompt,
     * this processes Darwin's kernel PENDIN without changing any terminal settings.
     */
    public static async Task<PtyTuiHarness> StartOwnedTerminalAsync(bool copyRealConfiguration)
    {
        var home = RealHostProcesses.CreateHome("r");
        var host = new PtyHost();
        try
        {
            if (copyRealConfiguration)
                RealHostProcesses.CopyRealConfiguration(home);
            var session = await host.StartAsync(new PtyStartInfo
            {
                FileName = "/bin/sh",
                Arguments = ["-c", """
                    stty -g > "$1/termios-before" && stty -a >> "$1/termios-before" || exit 125
                    tty > "$1/terminal.tmp" && mv "$1/terminal.tmp" "$1/terminal" || exit 125
                    printf 'DSH-RAW-MAIN-SCREEN\r\n'
                    "$2" tui
                    status=$?
                    printf '%s\n' "$status" > "$1/exit-code.tmp"
                    mv "$1/exit-code.tmp" "$1/exit-code"
                    read -r restored
                    stty -g > "$1/termios-after.tmp" && stty -a >> "$1/termios-after.tmp" || exit 125
                    mv "$1/termios-after.tmp" "$1/termios-after"
                    read -r release
                    exit "$status"
                    """, "raw-terminal", home, RealHostProcesses.Executable],
                Rows = 40,
                Columns = 140,
                Home = home,
                WorkingDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")),
                Environment = new Dictionary<string, string?>
                {
                    [HarnessHome.HomeEnv] = home,
                    [HarnessHome.ChildHomeEnv] = home,
                    ["LC_ALL"] = "C",
                    ["DSH_PTY_CHILD"] = "1",
                    ["TERM"] = "xterm-256color",
                },
            }, cancellationToken: TestContext.Current.CancellationToken);
            return new PtyTuiHarness(host, session, home);
        }
        catch
        {
            host.Dispose();
            TempTree.Delete(home);
            throw;
        }
    }

    public string Snapshot()
    {
        lock (_text)
            return _text.ToString();
    }

    public async Task<string> WaitForAsync(string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var text = Snapshot();
            if (text.Contains(marker, StringComparison.Ordinal))
                return text;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return text;
            var slice = remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200);
            await _signal.WaitAsync(slice, TestContext.Current.CancellationToken);
        }
    }

    public async Task<char[,]> WaitForScreenAsync(Func<char[,], bool> isReady, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var screen = ReadScreen();
            var remaining = deadline - DateTime.UtcNow;
            if (isReady(screen) || remaining <= TimeSpan.Zero)
                return screen;
            await _signal.WaitAsync(remaining, TestContext.Current.CancellationToken);
        }
    }

    public async Task WriteTextAsync(string text)
        => await Session.WriteAsync(Encoding.UTF8.GetBytes(text), TestContext.Current.CancellationToken);

    public async Task WriteBytesAsync(params byte[] bytes)
        => await Session.WriteAsync(bytes, TestContext.Current.CancellationToken);

    /** Consume the production PTY snapshot at its current geometry, including both screen buffers. */
    public VtScreen ReadTerminalScreen()
    {
        var size = _host.List().Single();
        var terminal = new VtScreen(size.Columns, size.Rows);
        terminal.Feed(Session.Snapshot());
        return terminal;
    }

    public char[,] ReadScreen()
    {
        var terminal = ReadTerminalScreen();
        var screen = new char[terminal.Width, terminal.Height];
        for (var y = 0; y < terminal.Height; y++)
        {
            var row = terminal.Row(y);
            for (var x = 0; x < row.Length; x++)
                screen[x, y] = row[x].Character is '\0' ? ' ' : row[x].Character;
        }
        return screen;
    }

    public void SaveTranscript()
    {
        var directory = Path.GetDirectoryName(TranscriptPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);
        File.WriteAllText(TranscriptPath, Snapshot().Replace("\u001b", "\\e"), Encoding.UTF8);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _host.StopAsync(Session.Id.ToString()).GetAwaiter().GetResult();
        _pump.GetAwaiter().GetResult();
        _signal.Dispose();
        _host.Dispose();
        // 本夹具起的 TUI 只是 proxy: 真正的会话常驻 daemon 且以本夹具的 home 为家, 不结束它会占住日志目录导致删除失败。
        StopResidentSessions();
        for (var attempt = 0; attempt < 20 && Directory.Exists(Home); attempt++)
        {
            try
            {
                Directory.Delete(Home, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    /** 结束本夹具 home 下的常驻会话(daemon 里 Home 归属为本 home 的那些)。 */
    private void StopResidentSessions()
    {
        try
        {
            foreach (var session in PtyDaemonClient.ListAsync().GetAwaiter().GetResult()
                         .Where(candidate => string.Equals(candidate.Home, Home, StringComparison.OrdinalIgnoreCase)))
            {
                for (var press = 0; press < 2; press++)
                {
                    try
                    {
                        using var keys = new MemoryStream([0x03]);
                        PtyDaemonClient.AttachAsync(session.Id, keys, Stream.Null).GetAwaiter().GetResult();
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    Thread.Sleep(50);
                }

                for (var attempt = 0; attempt < 40; attempt++)
                {
                    var status = PtyDaemonClient.ListAsync().GetAwaiter().GetResult()
                        .FirstOrDefault(candidate => candidate.Id == session.Id)
                        ?.Status;
                    if (status is null || !string.Equals(status, "Running", StringComparison.Ordinal))
                        break;
                    Thread.Sleep(25);
                }
            }
        }
        catch (Exception)
        {
            // daemon 未运行: 没有常驻会话需要收尾
        }
    }

    private async Task PumpAsync()
    {
        Volatile.Write(ref _outputPumpStarted, 1);
        var buffer = new byte[4096];
        while (!_cancellation.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await Session.ReadAsync(buffer, _cancellation.Token);
            }
            catch (Exception)
            {
                // 子进程退出(Unix 上 EIO)或被取消
                return;
            }
            if (read == 0)
            {
                // 无数据(ConPTY 空闲时也返回 0), 不能当成 EOF
                await Task.Delay(5);
                continue;
            }
            lock (_text)
                _text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            _signal.Release();
        }
    }

    /** WSL 的 /mnt(drvfs) 绑定域套接字返回 Operation not supported; 探测一次决定能否在该目录跑真实应用。 */
    private static bool SupportsUnixSockets(string directory)
    {
        if (OperatingSystem.IsWindows())
            return true;
        var probe = Path.Combine(directory, $"s{Guid.NewGuid():N}"[..9]);
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(probe));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(probe))
                File.Delete(probe);
        }
    }

    /**
     * 隔离 home(项目内 artifacts020 下, 不污染用户目录): 复制真实 home 的配置与凭据, 保证插件能解析。
     * Linux 上域套接字路径上限 108 字符, home 必须短: 用 artifacts020 下的 8 位目录名而非 GUID 全称。
     */
    private static string PrepareIsolatedHome()
    {
        var home = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts020", $"h{Guid.NewGuid():N}"[..9]));
        RealHostProcesses.CopyRealConfiguration(home);

        // 用例断言的是默认布局几何: 去掉上一层会话里拖出来的 TUI 布局参数(侧栏宽度/输入栏高度),
        // 否则同一用例会因为开发机上拖过侧栏而失败。
        var harnessHome = new HarnessHome(home);
        var settings = HarnessSettings.Load(harnessHome);
        if (settings.Plugins.TryGetValue(TuiSettings.Package, out var tui))
        {
            tui.Parameters.Remove("sidebarWidth");
            tui.Parameters.Remove("inputHeight");
            settings.SavePlugins(harnessHome);
        }

        return home;
    }

}
