using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.Services;

/**
 * 用 ssh 起远端 `dsharp host --serve --stdio`, 把它当成 stdio-over-SSH 的帧通道, 连成 IRemoteHost。
 * 远端命令叫 dsharp 而不是 dsh: Ubuntu 里 `dsh` 是 "dancer's shell / distributed shell" 包, 会重名。
 * 认证: 密钥对(-i)、ssh-agent(默认)、密码(SSH_ASKPASS 非交互); 可选代理经 ProxyCommand。
 * 另提供只在远端跑一条命令 / scp 上传的辅助, 供自动部署(RemoteDsharpInstaller)复用同一套认证。
 */
public static class SshRemoteWorkspaceLauncher
{
    public sealed record Connection(RemoteHostClient Client, Process Process) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            try
            {
                if (!Process.HasExited)
                    Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            Process.Dispose();
        }
    }

    public static Task<Connection> ConnectAsync(SshWorkspace workspace, CancellationToken cancellationToken)
        => ConnectAsync(workspace, hostCommandPath: null, cancellationToken);

    /** hostCommandPath 非空时用它作为远端宿主可执行文件(绝对路径); 为空则按“远端目录/ PATH”推导。 */
    public static async Task<Connection> ConnectAsync(SshWorkspace workspace, string? hostCommandPath, CancellationToken cancellationToken)
    {
        if (workspace.Auth == SshAuth.Password && string.IsNullOrEmpty(workspace.Password))
            throw new InvalidOperationException("认证方式为“password”但密码为空: 请在设置页填写密码（注意区分大小写、是否有尾部空格）。");

        var startInfo = BuildSshStartInfo(workspace, out var askPass);
        startInfo.ArgumentList.Add(HarnessCommand(workspace, hostCommandPath));
        try
        {
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start ssh");
            // 必须持续排空 stderr: 远端宿主/ssh 往 stderr 写日志, 管道填满后 ssh 会阻塞, 表现为握手卡死。
            var stderr = new StringBuilder();
            _ = DrainAsync(process.StandardError, stderr);
            try
            {
                var stream = new StandardIoDuplexStream(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
                var client = await RemoteHostClient.ConnectAsync(stream, token: null, cancellationToken).ConfigureAwait(false);
                return new Connection(client, process);
            }
            catch (Exception error)
            {
                Kill(process);
                throw new IOException($"ssh {workspace.User}@{workspace.Host} 失败（认证方式 {workspace.Auth}）: {Describe(stderr)}", error);
            }
        }
        finally
        {
            if (askPass is not null)
                TryDelete(askPass);
        }
    }

    /** 后台把 stderr 读进有界缓冲(排空管道, 避免背压死锁), 供失败时汇报。 */
    private static async Task DrainAsync(StreamReader reader, StringBuilder sink)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (sink)
                {
                    if (sink.Length < 8192)
                        sink.Append(buffer, 0, read);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static string Describe(StringBuilder sink)
    {
        lock (sink)
        {
            return sink.Length == 0 ? "(无 stderr 输出)" : sink.ToString().Trim();
        }
    }

    public static Task<string> TestAsync(SshWorkspace workspace, CancellationToken cancellationToken)
        => TestAsync(workspace, hostCommandPath: null, cancellationToken);

    /** 仅测试连通性: 连接后读宿主信息与会话数即断开, 返回可读摘要; 失败抛出(消息含 ssh stderr)。 */
    public static async Task<string> TestAsync(SshWorkspace workspace, string? hostCommandPath, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(workspace, hostCommandPath, cancellationToken).ConfigureAwait(false);
        var info = await connection.Client.InfoAsync(cancellationToken).ConfigureAwait(false);
        var sessions = await connection.Client.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        var directory = string.IsNullOrEmpty(workspace.RemotePath) ? "~（家目录）" : workspace.RemotePath;
        return $"已连接 {workspace.User}@{workspace.Host}（{info.Platform}）· 远端目录 {directory} · {sessions.Count} 个会话";
    }

    /** 在远端执行一条命令并返回 stdout(不启动 harness); 非 0 退出抛异常(含 stderr)。 */
    public static async Task<string> RunRemoteAsync(SshWorkspace workspace, string command, CancellationToken cancellationToken)
    {
        var startInfo = BuildSshStartInfo(workspace, out var askPass);
        startInfo.ArgumentList.Add(command);
        try
        {
            return await RunProcessAsync(startInfo, workspace, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (askPass is not null)
                TryDelete(askPass);
        }
    }

    /** 用 scp 上传本地文件到远端(认证与 ssh 一致); remotePath 支持 `~`。 */
    public static async Task UploadAsync(SshWorkspace workspace, string localPath, string remotePath, CancellationToken cancellationToken)
    {
        var startInfo = BuildScpStartInfo(workspace, out var askPass);
        startInfo.ArgumentList.Add(localPath);
        startInfo.ArgumentList.Add($"{workspace.User}@{workspace.Host}:{remotePath}");
        try
        {
            await RunProcessAsync(startInfo, workspace, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (askPass is not null)
                TryDelete(askPass);
        }
    }

    private static async Task<string> RunProcessAsync(ProcessStartInfo startInfo, SshWorkspace workspace, CancellationToken cancellationToken)
    {
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start process");
        using (process)
        {
            // 命令式调用(远端执行/scp)不需要 stdin: 必须显式关闭, 否则 Win32-OpenSSH 会一直等 stdin EOF, 表现为卡死。
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            var detail = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException($"{workspace.User}@{workspace.Host} 远端命令失败: {(string.IsNullOrWhiteSpace(detail) ? "(无 stderr)" : detail.Trim())}");
            return text;
        }
    }

    /** 远端启动 harness: 指定了 hostCommandPath 就用它(路径原样, 交由远端 shell); 否则按“远端目录”用 ./dsharp, 或 PATH 里的 dsharp。 */
    private static string HarnessCommand(SshWorkspace workspace, string? hostCommandPath)
    {
        // 指定了绝对路径时不做 POSIX 单引号转义, 只在含空白时加双引号(cmd 与 POSIX 都接受): 兼容 Windows 远端。
        if (!string.IsNullOrEmpty(hostCommandPath))
            return $"{NeutralQuote(hostCommandPath)} host --serve --stdio";
        var launcher = string.IsNullOrEmpty(workspace.RemotePath) ? "dsharp" : "./dsharp";
        var prefix = string.IsNullOrEmpty(workspace.RemotePath) ? "" : $"cd {Quote(workspace.RemotePath)} && ";
        return $"{prefix}{launcher} host --serve --stdio";
    }

    /** 只在含空白时用双引号包裹(cmd / POSIX 都接受); 否则原样。 */
    private static string NeutralQuote(string value)
        => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    private static ProcessStartInfo BuildSshStartInfo(SshWorkspace workspace, out string? askPass)
    {
        var startInfo = new ProcessStartInfo("ssh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var arguments = startInfo.ArgumentList;
        arguments.Add("-T"); // 不要远端 TTY: 帧通道走干净管道。
        arguments.Add("-p");
        arguments.Add(workspace.Port.ToString());
        AddCommonOptions(arguments, workspace);
        askPass = ApplyAskPass(startInfo, workspace);
        arguments.Add($"{workspace.User}@{workspace.Host}");
        return startInfo;
    }

    private static ProcessStartInfo BuildScpStartInfo(SshWorkspace workspace, out string? askPass)
    {
        var startInfo = new ProcessStartInfo("scp")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var arguments = startInfo.ArgumentList;
        arguments.Add("-q");
        arguments.Add("-P");
        arguments.Add(workspace.Port.ToString());
        AddCommonOptions(arguments, workspace);
        askPass = ApplyAskPass(startInfo, workspace);
        return startInfo;
    }

    private static void AddCommonOptions(Collection<string> arguments, SshWorkspace workspace)
    {
        arguments.Add("-o");
        arguments.Add("BatchMode=" + (string.IsNullOrEmpty(workspace.Password) ? "yes" : "no"));
        arguments.Add("-o");
        arguments.Add("StrictHostKeyChecking=accept-new");
        arguments.Add("-o");
        arguments.Add("ConnectTimeout=15");
        arguments.Add("-o");
        arguments.Add("ServerAliveInterval=15");
        arguments.Add("-o");
        arguments.Add("ServerAliveCountMax=4");
        if (workspace.Auth == SshAuth.Key && workspace.KeyPath is { Length: > 0 } key)
        {
            arguments.Add("-i");
            arguments.Add(key);
            arguments.Add("-o");
            arguments.Add("IdentitiesOnly=yes");
        }

        if (workspace.Proxy is { Length: > 0 } proxy)
        {
            arguments.Add("-o");
            arguments.Add($"ProxyCommand={proxy}");
        }
    }

    private static string? ApplyAskPass(ProcessStartInfo startInfo, SshWorkspace workspace)
    {
        if (workspace.Auth != SshAuth.Password || workspace.Password is not { Length: > 0 } password)
            return null;
        var askPass = WriteAskPass(password);
        startInfo.Environment["SSH_ASKPASS"] = askPass;
        startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        startInfo.Environment["DISPLAY"] = "dsh:0";
        return askPass;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        process.Dispose();
    }

    private static string WriteAskPass(string password)
    {
        // SSH_ASKPASS 必须是宿主平台可执行文件: Windows 用 .cmd, Unix 用 .sh(否则 ssh 无法执行, 密码认证失效)。
        if (OperatingSystem.IsWindows())
        {
            var command = Path.Combine(Path.GetTempPath(), $"dsh-askpass-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(command, $"@echo off\r\necho {password}\r\n", Encoding.ASCII);
            return command;
        }

        var path = Path.Combine(Path.GetTempPath(), $"dsh-askpass-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/sh\nprintf '%s\\n' {Quote(password)}\n", new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /** POSIX 单引号转义, 避免密码里的引号/空白破坏脚本。 */
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
