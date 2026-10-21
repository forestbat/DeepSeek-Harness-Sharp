using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using Dsh.Transport;

namespace Dsh.Fleet;

/** OpenSSH authentication, command capture, stdio channels and uploads; no deployment decisions. */
public sealed class SshTransport(MachineSpec machine, ISshTransportAdapter? adapter = null) : IRemoteMachineTransport
{
    public MachineSpec Machine => machine;

    internal SshCommandChannel OpenCommand(string command)
    {
        RequirePassword(machine);
        var startInfo = BuildSshStartInfo(machine, out var askPass, adapter: adapter);
        startInfo.ArgumentList.Add(command);
        try
        {
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start ssh");
            var stderr = new StringBuilder();
            _ = DrainAsync(process.StandardError, stderr);
            return new SshCommandChannel(process, askPass, () => Describe(stderr));
        }
        catch
        {
            if (askPass is not null)
                TryDelete(askPass);
            throw;
        }
    }

    internal static (ProcessStartInfo StartInfo, string? AskPass, string AuthenticationMarker) CreateProxyStart(MachineSpec machine, DnsEndPoint endpoint, int? localPort = null, ISshTransportAdapter? adapter = null)
    {
        RequirePassword(machine);
        var marker = $"dsh-ssh-auth-{Guid.NewGuid():N}";
        var startInfo = BuildSshStartInfo(machine, out var askPass, marker, localPort is not null, adapter);
        var host = endpoint.Host.Contains(':') && !endpoint.Host.StartsWith('[') ? $"[{endpoint.Host}]" : endpoint.Host;
        if (localPort is { } port)
        {
            startInfo.ArgumentList.Insert(startInfo.ArgumentList.Count - 1, "-N");
            startInfo.ArgumentList.Insert(startInfo.ArgumentList.Count - 1, "-L");
            startInfo.ArgumentList.Insert(startInfo.ArgumentList.Count - 1, $"127.0.0.1:{port}:{host}:{endpoint.Port}");
        }
        else
        {
            startInfo.ArgumentList.Insert(startInfo.ArgumentList.Count - 1, "-W");
            startInfo.ArgumentList.Insert(startInfo.ArgumentList.Count - 1, $"{host}:{endpoint.Port}");
        }
        return (startInfo, askPass, marker);
    }


    private static void RequirePassword(MachineSpec workspace)
    {
        if (workspace.Auth == SshAuth.Password && string.IsNullOrEmpty(workspace.Password))
            throw new InvalidOperationException("认证方式为“password”但密码为空: 请在设置页填写密码（注意区分大小写、是否有尾部空格）。");
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

    /** 在远端执行一条命令并返回 stdout(不启动 harness); 非 0 退出抛异常(含 stderr)。 */
    public async Task<string> RunAsync(string command, CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(command, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? result.StdOut
            : throw new IOException($"{machine.Target} 远端命令失败: {result.FailureDetail}");
    }

    /** 在远端执行一条命令, 原样返回退出码与 stdout/stderr(不抛异常), 供需要区分“ssh 失败”与“命令失败”的调用方使用。 */
    public async Task<RemoteProcessResult> CaptureAsync(string command, CancellationToken cancellationToken)
    {
        RequirePassword(machine);
        var startInfo = BuildSshStartInfo(machine, out var askPass, adapter: adapter);
        startInfo.ArgumentList.Add(command);
        try
        {
            var result = await CaptureProcessAsync(startInfo, cancellationToken).ConfigureAwait(false);
            return result with { TransportFailed = result.ExitCode == 255 };
        }
        finally
        {
            if (askPass is not null)
                TryDelete(askPass);
        }
    }

    /** A command line a person can paste into a POSIX shell to run remoteCommand on the machine with the same port, key, proxy and options. */
    public string CommandLine(string remoteCommand)
    {
        Collection<string> arguments = [];
        AddCommonOptions(arguments, machine, adapter);
        var line = new StringBuilder($"ssh -p {machine.Port}");
        foreach (var argument in arguments)
            line.Append($" {Quote(argument)}");
        line.Append($" {Quote($"{machine.User}@{machine.ConnectionHost}")} {Quote(remoteCommand)}");
        return line.ToString();
    }

    /** 用 scp 上传本地文件到远端(认证与 ssh 一致); remotePath 支持 `~`。 */
    public async Task UploadAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        var workspace = machine;
        RequirePassword(workspace);
        var startInfo = BuildScpStartInfo(workspace, out var askPass, adapter);
        startInfo.ArgumentList.Add(localPath);
        startInfo.ArgumentList.Add($"{workspace.User}@{workspace.ConnectionHost}:{remotePath}");
        try
        {
            var result = await CaptureProcessAsync(startInfo, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new IOException($"{workspace.Target} scp 上传失败: {result.FailureDetail}");
        }
        finally
        {
            if (askPass is not null)
                TryDelete(askPass);
        }
    }

    internal static async Task<RemoteProcessResult> CaptureProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start process");
        using (process)
        {
            // 命令式调用(远端执行/scp)不需要 stdin: 必须显式关闭, 否则 Win32-OpenSSH 会一直等 stdin EOF, 表现为卡死。
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw;
            }
            return new RemoteProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
    }


    private static ProcessStartInfo BuildSshStartInfo(MachineSpec workspace, out string? askPass, string? authenticationMarker = null, bool localForward = false, ISshTransportAdapter? adapter = null)
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
        if (authenticationMarker is not null)
        {
            foreach (var option in workspace.SshOptions ?? [])
            {
                var key = option.AsSpan().TrimStart();
                var separator = key.IndexOfAny("= \t\r\n");
                if (separator >= 0)
                    key = key[..separator];
                if (key.Equals("LocalCommand", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("PermitLocalCommand", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SSH outbound tunnels manage LocalCommand and PermitLocalCommand; remove these conflicting SSH options.");
            }
            // Only this target SSH executes the nonce command after authentication.
            // echo and descriptor redirection work in both the Unix shell and Windows system(cmd).
            // No user data, SSH tokens or credentials enter this fixed command.
            arguments.Add("-o");
            arguments.Add("PermitLocalCommand=yes");
            arguments.Add("-o");
            arguments.Add($"LocalCommand=echo {authenticationMarker} 1>&2");
            arguments.Add("-o");
            arguments.Add("ExitOnForwardFailure=yes");
            arguments.Add("-o");
            arguments.Add("ControlMaster=no");
            arguments.Add("-o");
            arguments.Add("ControlPath=none");
            if (localForward)
            {
                arguments.Add("-o");
                arguments.Add("ForkAfterAuthentication=no");
                arguments.Add("-o");
                arguments.Add("ClearAllForwardings=no");
            }
        }
        AddCommonOptions(arguments, workspace, adapter);
        askPass = ApplyAskPass(startInfo, workspace);
        arguments.Add($"{workspace.User}@{workspace.ConnectionHost}");
        return startInfo;
    }

    private static ProcessStartInfo BuildScpStartInfo(MachineSpec workspace, out string? askPass, ISshTransportAdapter? adapter)
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
        AddCommonOptions(arguments, workspace, adapter);
        askPass = ApplyAskPass(startInfo, workspace);
        return startInfo;
    }

    private static void AddCommonOptions(Collection<string> arguments, MachineSpec workspace, ISshTransportAdapter? adapter)
    {
        workspace.ValidateTransportSettings();
        if (workspace.Kind is not (MachineKinds.Local or MachineKinds.Ssh))
            AddAdapterOptions(arguments, workspace, adapter);
        foreach (var option in workspace.SshOptions ?? [])
        {
            arguments.Add("-o");
            arguments.Add(option);
        }

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

    private static void AddAdapterOptions(Collection<string> arguments, MachineSpec workspace, ISshTransportAdapter? adapter)
    {
        if (adapter is null || adapter.Kind != workspace.Kind)
            throw new NotSupportedException($"No SSH transport adapter is enabled for machine kind \"{workspace.Kind}\".");
        var configuration = adapter.Configure(workspace);
        if (configuration.DisableMultiplexing)
        {
            arguments.Add("-o");
            arguments.Add("ControlPath=none");
        }
        arguments.Add("-o");
        arguments.Add($"ProxyCommand={configuration.ProxyCommand}");
        arguments.Add("-o");
        arguments.Add($"HostKeyAlias={configuration.HostKeyAlias}");
    }

    private static string? ApplyAskPass(ProcessStartInfo startInfo, MachineSpec workspace)
    {
        if (workspace.Auth != SshAuth.Password || workspace.Password is not { Length: > 0 } password)
            return null;
        var askPass = WriteAskPass(password);
        startInfo.Environment["SSH_ASKPASS"] = askPass;
        startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        startInfo.Environment["DISPLAY"] = "dsh:0";
        return askPass;
    }

    internal static void Kill(Process process)
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
        OwnerOnlyFiles.Write(path, $"#!/bin/sh\nprintf '%s\\n' {Quote(password)}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /** POSIX 单引号转义, 避免密码里的引号/空白破坏脚本。 */
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal static void TryDelete(string path)
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
