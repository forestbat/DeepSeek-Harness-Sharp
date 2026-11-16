using System.Diagnostics;
using System.Text;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * `dsharp host serve --stdio` 是手机经 SSH exec 通道连宿主的入口。
 * 手机上实测会在握手阶段被断开, 但中间隔着 SSH.NET 的通道实现; 这里直接在本地起这个子进程跑同一套协议,
 * 就能判断是桥接本身不通, 还是只有跨越 SSH 通道时才出问题。
 */
public sealed class HostStdioBridgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /**
     * 桥接必须承受连续多次请求: 手机上第一个请求之后就断, 得先弄清是通道问题还是桥接本身只服务一次。
     * 这里在本地按同样顺序发多个请求, 本地也断就说明与 SSH 无关。
     */
    [Fact]
    public async Task StdioHost_ServesManyRequestsOnOneConnection()
    {
        var home = Dsh.Boot.HarnessStorage.ResolveDefaultHome().Root;
        Assert.True(File.Exists(Path.Combine(home, "run", "host.port")), $"没有运行中的 daemon: {home}");
        using var process = StartStdioHost(home);
        await using var duplex = new ProcessDuplexStream(process);
        await using var client = await RemoteHostClient.ConnectAsync(duplex, token: null, "stdio-test", Ct);

        var sessions = await client.ListSessionsAsync(Ct);
        Assert.NotNull(sessions);
        // 连续往返: 每一步都打印, 断在第几步一眼可见。
        for (var round = 1; round <= 5; round += 1)
        {
            await client.PingAsync(Ct);
            TestContext.Current.SendDiagnosticMessage($"round {round}: ping ok");
        }
    }

    [Fact]
    public async Task StdioHost_CompletesHelloAndListsSessions()
    {
        // 桥接模式把 stdio 接到常驻 daemon; 用当前用户的真实 home, 那里有正在运行的 daemon。
        var home = Dsh.Boot.HarnessStorage.ResolveDefaultHome().Root;
        Assert.True(File.Exists(Path.Combine(home, "run", "host.port")), $"没有运行中的 daemon: {home}");
        using var process = StartStdioHost(home);
        await using var duplex = new ProcessDuplexStream(process);
        // 先看子进程往 stdout 写了什么: 协议流里混进任何文字, 客户端都会把它当帧长读, 报"非法帧长度"。
        var sniff = new byte[4096];
        using (var fallback = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            fallback.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var got = await process.StandardOutput.BaseStream.ReadAsync(sniff, fallback.Token);
                var text = System.Text.Encoding.UTF8.GetString(sniff, 0, got);
                TestContext.Current.SendDiagnosticMessage($"stdout first {got}B: {text}");
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                TestContext.Current.SendDiagnosticMessage("stdout silent for 5s");
            }
        }
        await using var client = await RemoteHostClient.ConnectAsync(duplex, token: null, "stdio-test", Ct);

        var sessions = await client.ListSessionsAsync(Ct);
        Assert.NotNull(sessions);
    }

    /** 用同一份产物起子进程: 测试要跑的就是真实入口, 不能另写一条等价的启动路径。 */
    private static Process StartStdioHost(string root)
    {
        var hostDll = ResolveHostDll();
        var start = new ProcessStartInfo
        {
            // 用绝对路径: 子进程找不到 dotnet 时会把"无法执行"写进 stdout, 那会直接污染协议流。
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(hostDll);
        start.ArgumentList.Add("host");
        start.ArgumentList.Add("--serve");
        start.ArgumentList.Add("--stdio");
        start.Environment["DSH_HOME"] = root;
        start.Environment["DSH_BRIDGE_TRACE"] = "1";
        var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start the stdio host");
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None)) is not null)
                TestContext.Current.SendDiagnosticMessage($"bridge: {line}");
        });
        return process;
    }

    /**
     * 宿主入口 dll 来自正常的构建产物, 不在测试输出目录里。
     * 直接从仓库根下的构建产物中找, 找不到就明确失败, 而不是让子进程报"找不到命令"把协议流污染掉。
     */
    private static string ResolveHostDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeepSeek-Harness-Sharp.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("repository root not found");
        foreach (var build in new[] { "test-build", "direct-build", "ui-build" })
        {
            var candidate = Path.Combine(directory.FullName, "artifacts", build, "bin", "Dsh.GuiHost", "debug", "dsh-gui.dll");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("dsh-gui.dll not found under artifacts/*/bin/Dsh.GuiHost/debug");
    }

    /** 把子进程的 stdin/stdout 当成一条双向字节流。 */
    private sealed class ProcessDuplexStream(Process process) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => process.StandardOutput.BaseStream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => process.StandardInput.BaseStream.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => process.StandardInput.BaseStream.WriteAsync(buffer, cancellationToken);
        public override void Flush() => process.StandardInput.BaseStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
            base.Dispose(disposing);
        }
    }
}