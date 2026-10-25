using Dsh.Boot;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Host;

/** 按需启动独立 Host, GUI 或 stdio 桥接退出不终止其会话; 服务监管由用户自行配置。 */
public static class HostAutostart
{
    /** Kept open before composition and registry loading, so competing starts cannot replace a live endpoint or read a stale registry. */
    public static FileStream? TryAcquireInstance(HarnessHome home)
    {
        Directory.CreateDirectory(home.RunPath);
        try
        {
            return new FileStream(Path.Combine(home.RunPath, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
    /** 当前 dsharp 跑 `host serve` 的命令行: 框架依赖部署时进程是 dotnet, 要带上入口 dll。 */
    public static (string Executable, IReadOnlyList<string> Arguments) ServeCommand(string? token = null)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("failed to resolve the current executable to start the host daemon");
        List<string> arguments = [];
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        if (token is { Length: > 0 })
            arguments.AddRange(["--token", token]);
        arguments.AddRange(["host", "serve"]);
        return (executable, arguments);
    }

    /** 按需启动 Host，并在启动进程存活时等待真实监听就绪。 */
    public static async Task EnsureRunningAsync(HarnessHome home, string? token, CancellationToken cancellationToken = default)
    {
        await using (var existing = await RemoteHostConnector.TryConnectClientAsync(home.Root, token, clientName: null, cancellationToken).ConfigureAwait(false))
        {
            if (existing is not null)
                return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var (executable, arguments) = ServeCommand(token);
        Dictionary<string, string?> environment = new() { [HarnessHome.HomeEnv] = home.Root };
        var startupLifetime = BackgroundProcess.Start(executable, arguments, Directory.GetCurrentDirectory(), environment);
        using var connection = await RemoteHostConnector.ConnectAsync(home.Root, cancellationToken, startupLifetime: startupLifetime);
    }
}
