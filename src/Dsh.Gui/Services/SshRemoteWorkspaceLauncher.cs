using System.Diagnostics;
using System.Text;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.Services;

/**
 * 用 ssh 起远端 `dsh host --serve --stdio`, 把它当成 stdio-over-SSH 的帧通道, 连成 IRemoteHost。
 * 认证: 密钥对(-i)、ssh-agent(默认)、密码(SSH_ASKPASS 非交互); 可选代理经 ProxyCommand。
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

    public static async Task<Connection> ConnectAsync(SshWorkspace workspace, CancellationToken cancellationToken)
    {
        string? askPass = null;
        var startInfo = new ProcessStartInfo("ssh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var arguments = startInfo.ArgumentList;
        arguments.Add("-T"); // 不要远端 TTY: 帧通道走干净管道。
        arguments.Add("-p");
        arguments.Add(workspace.Port.ToString());
        arguments.Add("-o");
        arguments.Add("BatchMode=" + (string.IsNullOrEmpty(workspace.Password) ? "yes" : "no"));
        arguments.Add("-o");
        arguments.Add("StrictHostKeyChecking=accept-new");
        if (workspace.Auth == SshAuth.Key && workspace.KeyPath is { Length: > 0 } key)
        {
            arguments.Add("-i");
            arguments.Add(key);
            arguments.Add("-o");
            arguments.Add("IdentitiesOnly=yes");
        }

        if (workspace.Auth == SshAuth.Password && workspace.Password is { Length: > 0 } password)
        {
            askPass = WriteAskPass(password);
            startInfo.Environment["SSH_ASKPASS"] = askPass;
            startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            startInfo.Environment["DISPLAY"] = "dsh:0";
        }

        if (workspace.Proxy is { Length: > 0 } proxy)
        {
            arguments.Add("-o");
            arguments.Add($"ProxyCommand={proxy}");
        }

        arguments.Add($"{workspace.User}@{workspace.Host}");
        arguments.Add(string.IsNullOrEmpty(workspace.RemotePath)
            ? "dsh host --serve --stdio"
            : $"cd {Quote(workspace.RemotePath)} && ./dsh host --serve --stdio");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start ssh");
        try
        {
            var stream = new StandardIoDuplexStream(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            var client = await RemoteHostClient.ConnectAsync(stream, token: null, cancellationToken).ConfigureAwait(false);
            return new Connection(client, process);
        }
        catch (Exception error)
        {
            var detail = await ReadStandardErrorAsync(process).ConfigureAwait(false);
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
            throw new IOException($"ssh {workspace.User}@{workspace.Host} 失败: {detail}", error);
        }
        finally
        {
            if (askPass is not null)
                TryDelete(askPass);
        }
    }

    private static async Task<string> ReadStandardErrorAsync(Process process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var text = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? "(无 stderr 输出)" : text.Trim();
        }
        catch (Exception)
        {
            return "(未能读取 stderr)";
        }
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
