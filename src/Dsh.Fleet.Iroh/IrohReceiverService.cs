using System.Diagnostics;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Dsh.Fleet.Iroh;

public static partial class IrohReceiver
{
    private static string? _linuxBootId;
    private const int LinuxProcessStartTimeField = 22;

    internal static string ProcessStartIdentity(Process process)
    {
        if (!OperatingSystem.IsLinux())
            return process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Linux StartTime converts boot-relative time using a process-local wall-clock estimate.
        var stat = File.ReadAllText($"/proc/{process.Id}/stat");
        var fields = stat.AsSpan(stat.LastIndexOf(')') + 2);
        var field = 3;
        foreach (var range in fields.Split(' '))
            if (field++ == LinuxProcessStartTimeField)
                return $"{_linuxBootId ??= File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()}:{fields[range]}";
        throw new IOException($"Cannot read the kernel creation identity for receiver process {process.Id}.");
    }

    /** Service managers own process identity; the manager-less POSIX path checks executable and process creation time before every stop. */
    private sealed class ReceiverService(string root)
    {
        private readonly string name = ServiceName(root);
        private string ModePath => Path.Combine(root, "mode");
        private string Mode => File.Exists(ModePath) ? File.ReadAllText(ModePath).Trim() : "";
        private string UnitPath => Path.Combine(root, $"{name}.service");
        private string AgentPath => Path.Combine(root, $"{name}.plist");
        private string LoginAgentPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", $"{name}.plist");
        private string TaskPath => Path.Combine(root, $"{name}.xml");
        private string ProcessPath => Path.Combine(root, "process");
        private string LogPath => Path.Combine(root, "receiver.log");
        private string LaunchTarget => $"gui/{SystemUserId()}/{name}";

        public string Description => Mode switch
        {
            "systemd" => $"systemd user service {name}; survives logout/boots only when an administrator has enabled loginctl enable-linger {Environment.UserName}",
            "launchd" => $"launchd user agent {name}; starts at user login",
            "task" => $"Windows current-user scheduled task {name}; starts at user login, without elevation",
            "detached" => $"detached receiver; does not auto-start after reboot; rerun dsharp iroh-remote enable; log: {LogPath}",
            _ => "receiver is not enabled",
        };

        public async Task InstallAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            await RemoveAsync(cancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsLinux() && await HasUserManagerAsync(cancellationToken).ConfigureAwait(false))
                await InstallSystemdAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
            else if (OperatingSystem.IsMacOS() && (await RunCommandAsync("launchctl", ["print", $"gui/{SystemUserId()}"], cancellationToken).ConfigureAwait(false)).ExitCode == 0)
                await InstallLaunchAgentAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
            else if (OperatingSystem.IsWindows())
                await InstallTaskAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
            else
                await StartDetachedAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
        {
            if (Mode == "systemd")
                return (await RunCommandAsync("systemctl", ["--user", "is-active", name], cancellationToken).ConfigureAwait(false)).ExitCode == 0;
            if (Mode == "launchd")
            {
                var result = await RunCommandAsync("launchctl", ["print", LaunchTarget], cancellationToken).ConfigureAwait(false);
                return result.ExitCode == 0 && result.Output.Contains("state = running", StringComparison.Ordinal);
            }
            if (Mode == "task")
            {
                var command = $"$t=Get-ScheduledTask -TaskName '{name}' -ErrorAction SilentlyContinue; if ($null -eq $t -or $t.State -ne 'Running') {{ exit 1 }}";
                return (await RunCommandAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], cancellationToken).ConfigureAwait(false)).ExitCode == 0;
            }
            using var process = Mode == "detached" ? OwnedProcess() : null;
            return process is not null;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            switch (Mode)
            {
                case "systemd":
                    if (await HasOwnedSystemdUnitAsync(cancellationToken).ConfigureAwait(false))
                        await RequireCommandAsync("systemctl", ["--user", "stop", name], cancellationToken).ConfigureAwait(false);
                    break;
                case "launchd":
                    var agent = await RunCommandAsync("launchctl", ["print", LaunchTarget], cancellationToken).ConfigureAwait(false);
                    if (agent.ExitCode == 0)
                    {
                        if (!agent.Output.Contains($"path = {AgentPath}", StringComparison.Ordinal))
                            throw new IOException($"Refusing to stop an unrelated launch agent: {LaunchTarget}");
                        await RequireCommandAsync("launchctl", ["bootout", LaunchTarget], cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case "task":
                    await RequireTaskOwnershipAsync(cancellationToken).ConfigureAwait(false);
                    if (await IsRunningAsync(cancellationToken).ConfigureAwait(false))
                        await RequireCommandAsync("schtasks.exe", ["/End", "/TN", name], cancellationToken).ConfigureAwait(false);
                    break;
                case "detached":
                    using (var process = OwnedProcess())
                    {
                        if (process is not null)
                        {
                            process.Kill();
                            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                    File.Delete(ProcessPath);
                    break;
            }
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (!await IsRunningAsync(cancellationToken).ConfigureAwait(false))
                    return;
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            throw new IOException($"Could not stop {Description}.");
        }

        public async Task RemoveAsync(CancellationToken cancellationToken)
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);
            switch (Mode)
            {
                case "systemd":
                    if (await HasOwnedSystemdUnitAsync(cancellationToken).ConfigureAwait(false))
                        await RequireCommandAsync("systemctl", ["--user", "disable", name], cancellationToken).ConfigureAwait(false);
                    File.Delete(UnitPath);
                    await RequireCommandAsync("systemctl", ["--user", "daemon-reload"], cancellationToken).ConfigureAwait(false);
                    break;
                case "launchd":
                    File.Delete(LoginAgentPath);
                    File.Delete(AgentPath);
                    break;
                case "task":
                    await RequireCommandAsync("schtasks.exe", ["/Delete", "/TN", name, "/F"], cancellationToken).ConfigureAwait(false);
                    File.Delete(TaskPath);
                    break;
            }
            File.Delete(ModePath);
            File.Delete(ReadinessPath(root));
        }

        private static async Task<bool> HasUserManagerAsync(CancellationToken cancellationToken)
        {
            if (!Directory.Exists("/run/systemd/system"))
                return false;
            try
            {
                return (await RunCommandAsync("systemctl", ["--user", "show-environment"], cancellationToken).ConfigureAwait(false)).ExitCode == 0;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private async Task<bool> HasOwnedSystemdUnitAsync(CancellationToken cancellationToken)
        {
            var result = await RunCommandAsync("systemctl", ["--user", "show", name, "--property=FragmentPath", "--property=LoadState"], cancellationToken).ConfigureAwait(false);
            const string fragmentPrefix = "FragmentPath=";
            var owned = false;
            var hasFragment = false;
            var missing = false;
            foreach (var line in result.Output.AsSpan().EnumerateLines())
            {
                if (line.StartsWith(fragmentPrefix, StringComparison.Ordinal))
                {
                    var fragment = line[fragmentPrefix.Length..];
                    owned = fragment.SequenceEqual(UnitPath);
                    hasFragment = !fragment.IsEmpty;
                }
                missing |= line.SequenceEqual("LoadState=not-found");
            }
            if (result.ExitCode == 0 && owned)
                return true;
            if (missing && !hasFragment)
                return false;
            throw new IOException($"Refusing to stop {name}: its systemd FragmentPath is not the receiver's owned unit {UnitPath}.");
        }

        private async Task RequireTaskOwnershipAsync(CancellationToken cancellationToken)
        {
            var result = await RunCommandAsync("schtasks.exe", ["/Query", "/TN", name, "/XML"], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new IOException($"Cannot verify the receiver task {name}: {result.Output}");
            if (!File.Exists(TaskPath))
                throw new IOException($"Refusing to stop a task without its owned definition: {TaskPath}");
            var actual = XDocument.Parse(result.Output);
            var expected = XDocument.Load(TaskPath);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            foreach (var key in new[] { "Command", "Arguments", "UserId" })
                if (!actual.Descendants(ns + key).Select(element => element.Value).SequenceEqual(expected.Descendants(ns + key).Select(element => element.Value)))
                    throw new IOException($"Refusing to stop an unrelated scheduled task {name}: {key} differs.");
        }

        private async Task InstallSystemdAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var command = string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteSystemd));
            var text = $"[Unit]\nDescription=DeepSeek Harness iroh SSH receiver\n\n[Service]\nType=simple\nExecStart={command}\nRestart=on-failure\nRestartSec=2\nUMask=0077\nStandardOutput=journal\nStandardError=journal\n\n[Install]\nWantedBy=default.target\n";
            await File.WriteAllTextAsync(UnitPath, text, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(ModePath, "systemd", cancellationToken).ConfigureAwait(false);
            await RequireCommandAsync("systemctl", ["--user", "daemon-reload"], cancellationToken).ConfigureAwait(false);
            await RequireCommandAsync("systemctl", ["--user", "enable", UnitPath], cancellationToken).ConfigureAwait(false);
            await RequireCommandAsync("systemctl", ["--user", "restart", name], cancellationToken).ConfigureAwait(false);
        }

        private async Task InstallLaunchAgentAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var values = string.Join("", new[] { executable }.Concat(arguments).Select(value => $"<string>{SecurityElement.Escape(value)}</string>"));
            var text = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict><key>Label</key><string>{name}</string><key>ProgramArguments</key><array>{values}</array><key>RunAtLoad</key><true/><key>KeepAlive</key><true/><key>Umask</key><integer>63</integer><key>StandardOutPath</key><string>{SecurityElement.Escape(LogPath)}</string><key>StandardErrorPath</key><string>{SecurityElement.Escape(LogPath)}</string></dict></plist>";
            await File.WriteAllTextAsync(AgentPath, text, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(LoginAgentPath)!);
            if (File.Exists(LoginAgentPath))
            {
                if (new FileInfo(LoginAgentPath).LinkTarget != AgentPath)
                    throw new IOException($"Refusing to replace an unrelated launch agent: {LoginAgentPath}");
                File.Delete(LoginAgentPath);
            }
            File.CreateSymbolicLink(LoginAgentPath, AgentPath);
            await File.WriteAllTextAsync(ModePath, "launchd", cancellationToken).ConfigureAwait(false);
            await RequireCommandAsync("launchctl", ["bootstrap", $"gui/{SystemUserId()}", AgentPath], cancellationToken).ConfigureAwait(false);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task InstallTaskAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("Cannot resolve the current Windows user.");
            var command = SecurityElement.Escape(executable);
            var args = SecurityElement.Escape(string.Join(' ', arguments.Select(QuoteWindows)));
            var text = $"<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId></LogonTrigger></Triggers><Principals><Principal id=\"Author\"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings><Actions Context=\"Author\"><Exec><Command>{command}</Command><Arguments>{args}</Arguments></Exec></Actions></Task>";
            await File.WriteAllTextAsync(TaskPath, text, Encoding.Unicode, cancellationToken).ConfigureAwait(false);
            await RequireCommandAsync("schtasks.exe", ["/Create", "/TN", name, "/XML", TaskPath, "/F"], cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(ModePath, "task", cancellationToken).ConfigureAwait(false);
            await RequireCommandAsync("schtasks.exe", ["/Run", "/TN", name], cancellationToken).ConfigureAwait(false);
        }

        private async Task StartDetachedAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var command = string.Join(' ', new[] { executable }.Concat(arguments).Select(RemoteShell.Quote));
            // Ignoring HUP directly also works when macOS nohup cannot detach from a headless SSH console.
            var script = $"umask 077; trap '' HUP; {command} </dev/null >>{RemoteShell.Quote(LogPath)} 2>&1 & echo $!";
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RunCommandAsync("/bin/sh", ["-c", script], CancellationToken.None).ConfigureAwait(false);
            if (result.ExitCode != 0 || !int.TryParse(result.Output, out var pid))
                throw new IOException($"Could not start the detached receiver: {result.Output}");
            using var process = Process.GetProcessById(pid);
            var identity = $"{pid}\n{ProcessStartIdentity(process)}\n{Path.GetFullPath(executable)}\n";
            await File.WriteAllTextAsync(ProcessPath, identity, CancellationToken.None).ConfigureAwait(false);
            await File.WriteAllTextAsync(ModePath, "detached", CancellationToken.None).ConfigureAwait(false);
        }

        private Process? OwnedProcess()
        {
            if (!File.Exists(ProcessPath))
                return null;
            var identity = File.ReadAllLines(ProcessPath);
            if (identity.Length != 3 || !int.TryParse(identity[0], out var pid) || string.IsNullOrWhiteSpace(identity[1]))
                throw new IOException($"Invalid receiver process identity in {ProcessPath}; refusing to stop an unverified process.");
            Process? process = null;
            try
            {
                process = Process.GetProcessById(pid);
                if (!process.HasExited && string.Equals(ProcessStartIdentity(process), identity[1], StringComparison.Ordinal) && string.Equals(process.MainModule?.FileName, identity[2], StringComparison.Ordinal))
                    return process;
                process.Dispose();
                return null;
            }
            catch (IOException error) when ((error is FileNotFoundException or DirectoryNotFoundException) && process is { HasExited: true })
            {
                process.Dispose();
                return null;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                process?.Dispose();
                return null;
            }
        }

        private static uint SystemUserId() => OperatingSystem.IsWindows() ? 0 : GetUserId();

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
        private static extern uint GetUserId();

        private static string QuoteSystemd(string value)
            => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal).Replace("$", "$$", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)}\"";

        private static string QuoteWindows(string value)
        {
            var builder = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in value)
            {
                if (c == '\\')
                    slashes++;
                else
                {
                    builder.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
                    slashes = 0;
                }
            }
            return builder.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
