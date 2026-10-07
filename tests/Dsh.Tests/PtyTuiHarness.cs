using System.Net.Sockets;
using System.Text;
using Dsh.Boot;
using Dsh.Pty;
using Dsh.Tui.Services;

namespace Dsh.Tests;

/**
 * 真实 dsh TUI 跑在**工程自身 PTY**(PtyHost/PtySession)上的测试夹具:
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

    private PtyTuiHarness(PtyHost host, PtySession session, string home)
    {
        _host = host;
        Session = session;
        Home = home;
        _pump = Task.Run(PumpAsync);
    }

    public PtySession Session { get; }

    public string Home { get; }

    /** 找不到已构建的 TUI 可执行文件时返回 null(调用方直接 return); 环境缺配置/不支持 AF_UNIX 时 Assert.Skip。 */
    public static async Task<PtyTuiHarness?> StartAsync(params string[] extraArguments)
    {
        var executable = FindTuiExecutable();
        if (executable is null)
            return null;
        var realHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        if (!File.Exists(Path.Combine(realHome, "settings.yaml")))
            Assert.Skip($"环境缺少 {realHome}/settings.yaml, 无法启动 dsh");
        var home = PrepareIsolatedHome(realHome);
        if (!SupportsUnixSockets(home))
            Assert.Skip($"{home} 所在文件系统不支持 AF_UNIX(如 WSL 的 /mnt drvfs); 请把仓库放在原生文件系统上运行");

        var host = new PtyHost();
        try
        {
            var arguments = new List<string> { "tui", "--home", home };
            arguments.AddRange(extraArguments);
            var session = await host.StartAsync(new PtyStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                Rows = Rows,
                Columns = Columns,
            }, cancellationToken: TestContext.Current.CancellationToken);
            return new PtyTuiHarness(host, session, home);
        }
        catch
        {
            host.Dispose();
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

    public async Task WriteTextAsync(string text)
        => await Session.WriteAsync(Encoding.UTF8.GetBytes(text), TestContext.Current.CancellationToken);

    public async Task WriteBytesAsync(params byte[] bytes)
        => await Session.WriteAsync(bytes, TestContext.Current.CancellationToken);

    /** 把当前输出解析成字符屏(VirtualTerminal 忽略私有模式, 重绘按 CUP 覆盖, 结果即最近一屏)。 */
    public char[,] ReadScreen()
    {
        var terminal = new VirtualTerminal(Columns, Rows);
        terminal.Feed(Snapshot());
        return terminal.Screen;
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
        _signal.Dispose();
        _ = _pump;
        _host.StopAsync(Session.Id.ToString()).GetAwaiter().GetResult();
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

    /** 结束本夹具 home 下的常驻会话(daemon 里 Command 含本 home 的那些)。 */
    private void StopResidentSessions()
    {
        try
        {
            foreach (var session in PtyDaemonClient.ListAsync().GetAwaiter().GetResult()
                         .Where(candidate => candidate.Command.Contains(Home, StringComparison.OrdinalIgnoreCase)))
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
    private static string PrepareIsolatedHome(string realHome)
    {
        var home = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts020", $"h{Guid.NewGuid():N}"[..9]));
        Directory.CreateDirectory(home);
        foreach (var name in new[] { "settings.yaml", "profiles" })
        {
            var source = Path.Combine(realHome, name);
            if (File.Exists(source))
                File.Copy(source, Path.Combine(home, name), overwrite: true);
            else if (Directory.Exists(source))
                CopyDirectory(source, Path.Combine(home, name));
        }

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

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string? FindTuiExecutable()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var name = OperatingSystem.IsWindows() ? "DeepSeek-Harness-Sharp.exe" : "DeepSeek-Harness-Sharp";
        var path = Path.Combine(root, "DeepSeek-Harness-Sharp", "bin", "Debug", "net10.0", name);
        return File.Exists(path) ? path : null;
    }
}
