using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dsh.Boot;

namespace Dsh.Fleet.Iroh;

public sealed record IrohReceiverStatus(string EndpointId, bool Running, string ServiceDescription);

/** Owns only the current user's iroh receiver and its persistent identity, never sshd or the workspace Host. */
public static partial class IrohReceiver
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(250);
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static async Task<IrohReceiverStatus> EnableAsync(HarnessHome home, int sshPort, IReadOnlyList<string> relayUrls, Guid? activation, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sshPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sshPort, 65535);
        MachineSpec.ValidateRelayUrls(relayUrls);
        var executable = IrohSshComponent.RequireExecutable();
        var root = ReceiverRoot(home);
        await PreparePrivateDirectoryAsync(root, cancellationToken).ConfigureAwait(false);
        using var operation = AcquireOperation(root);
        ThrowIfActivationCanceled(root, activation);
        var service = new ReceiverService(root);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("iroh receiver setup supports Linux, macOS, and Windows.");
        if (activation is not null && await service.IsRunningAsync(cancellationToken).ConfigureAwait(false))
            return new IrohReceiverStatus(await ReadEndpointAsync(root, cancellationToken).ConfigureAwait(false), true, service.Description);
        await RequireSshServerAsync(sshPort, cancellationToken).ConfigureAwait(false);
        var activationPath = Path.Combine(root, "activation");
        RejectLink(activationPath);
        await File.WriteAllTextAsync(activationPath, activation?.ToString("N") ?? "", cancellationToken).ConfigureAwait(false);
        try
        {
            await service.InstallAsync(executable, ServerArguments(root, sshPort, relayUrls), cancellationToken).ConfigureAwait(false);
            var status = await WaitUntilReadyAsync(executable, root, service, relayUrls, cancellationToken).ConfigureAwait(false);
            ThrowIfActivationCanceled(root, activation);
            return status;
        }
        catch
        {
            await service.RemoveAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<IrohReceiverStatus> InfoAsync(HarnessHome home, CancellationToken cancellationToken)
    {
        var root = ReceiverRoot(home);
        var service = new ReceiverService(root);
        if (!Directory.Exists(root))
            return new IrohReceiverStatus("", false, service.Description);
        using var operation = AcquireOperation(root);
        var endpoint = await ReadEndpointAsync(root, cancellationToken).ConfigureAwait(false);
        return new IrohReceiverStatus(endpoint, await service.IsRunningAsync(cancellationToken).ConfigureAwait(false), service.Description);
    }

    public static async Task<IrohReceiverStatus> DisableAsync(HarnessHome home, Guid? activation, CancellationToken cancellationToken)
    {
        var root = ReceiverRoot(home);
        var service = new ReceiverService(root);
        if (activation is { } canceled)
        {
            PreparePrivateDirectory(root);
            var marker = Path.Combine(root, $"cancelled-{canceled:N}");
            RejectLink(marker);
            await File.WriteAllTextAsync(marker, "", cancellationToken).ConfigureAwait(false);
        }
        if (!Directory.Exists(root))
            return new IrohReceiverStatus("", false, service.Description);
        using var operation = AcquireOperation(root);
        var activationPath = Path.Combine(root, "activation");
        RejectLink(activationPath);
        if (activation is not null && (!File.Exists(activationPath) || await File.ReadAllTextAsync(activationPath, cancellationToken).ConfigureAwait(false) != activation.Value.ToString("N")))
            return new IrohReceiverStatus(await ReadEndpointAsync(root, cancellationToken).ConfigureAwait(false), await service.IsRunningAsync(cancellationToken).ConfigureAwait(false), $"{service.Description}; activation does not own this receiver");
        await service.RemoveAsync(cancellationToken).ConfigureAwait(false);
        File.Delete(activationPath);
        var endpoint = await ReadEndpointAsync(root, cancellationToken).ConfigureAwait(false);
        return new IrohReceiverStatus(endpoint, false, $"{service.Description}; disabled; identity retained");
    }

    internal static string ReceiverRoot(HarnessHome home) => Path.Combine(Path.GetFullPath(home.Root), "iroh-receiver");

    internal static string ReadinessPath(string root) => Path.Combine(root, "ready");

    internal static string ServiceName(string root)
        => $"dsharp-iroh-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root))))[..16]}";

    internal static string[] ServerArguments(string root, int sshPort, IReadOnlyList<string> relayUrls)
        => ["server", "--persist", "--key-dir", Path.Combine(root, "identity"), "--ready-file", ReadinessPath(root), "--ssh-port", sshPort.ToString(System.Globalization.CultureInfo.InvariantCulture), .. relayUrls.SelectMany(url => new[] { "--relay-url", url })];


    private static FileStream AcquireOperation(string root)
    {
        RejectLink(root);
        var path = Path.Combine(root, "operation.lock");
        RejectLink(path);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException("Another receiver operation is active; wait for it to finish before changing the receiver.", error);
        }
    }

    private static Task PreparePrivateDirectoryAsync(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PreparePrivateDirectory(root);
        var identity = Path.Combine(root, "identity");
        PreparePrivateDirectory(identity);
        foreach (var path in Directory.EnumerateFileSystemEntries(identity))
        {
            RejectLink(path);
            if (OperatingSystem.IsWindows())
                ProtectWindowsPath(path);
            else
                File.SetUnixFileMode(path, PrivateFileMode);
        }
        var privateKey = File.Exists(Path.Combine(identity, "irohssh_ed25519"));
        var publicKey = File.Exists(Path.Combine(identity, "irohssh_ed25519.pub"));
        if (privateKey != publicKey)
            throw new IOException($"Incomplete receiver identity in {identity}; restore both identity files from backup rather than silently replacing the endpoint ID.");
        return Task.CompletedTask;
    }

    private static void ThrowIfActivationCanceled(string root, Guid? activation)
    {
        if (activation is { } id && File.Exists(Path.Combine(root, $"cancelled-{id:N}")))
            throw new IOException("This receiver activation was cancelled; refusing to enable it.");
    }

    private static void PreparePrivateDirectory(string path)
    {
        RejectLink(path);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            ProtectWindowsPath(path);
        }
        else
        {
            Directory.CreateDirectory(path, PrivateDirectoryMode);
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ProtectWindowsPath(string path)
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot resolve the current Windows user's SID.");
        if (Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException($"Receiver state must not be a symbolic link: {path}");
    }

    private static async Task RequireSshServerAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync("127.0.0.1", port, timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(socket.GetStream());
            await RequireSshBannerAsync(reader, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var instructions = OperatingSystem.IsWindows()
                ? "In an elevated PowerShell, install OpenSSH.Server with Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0, then Start-Service sshd and Set-Service sshd -StartupType Automatic."
                : OperatingSystem.IsMacOS()
                    ? "Enable Remote Login in System Settings > General > Sharing, and allow your login account."
                    : "Ask an administrator to install openssh-server and start its ssh/sshd service (for example sudo apt install openssh-server && sudo systemctl enable --now ssh on Debian/Ubuntu).";
            throw new IOException($"No SSH server banner is reachable at 127.0.0.1:{port}. {instructions} For a custom sshd port, pass --ssh-port. dsharp does not install, reconfigure, or stop sshd.", error);
        }
    }

    private static async Task RequireSshBannerAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        for (var lines = 0; lines < 50; lines++)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;
            if (line.StartsWith("SSH-2.0-", StringComparison.Ordinal) || line.StartsWith("SSH-1.99-", StringComparison.Ordinal))
                return;
        }
        throw new IOException("The listener did not send an SSH protocol banner.");
    }

    private static async Task<IrohReceiverStatus> WaitUntilReadyAsync(string executable, string root, ReceiverService service, IReadOnlyList<string> relayUrls, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadinessTimeout);
        var readyPath = ReadinessPath(root);
        try
        {
            // The native acknowledgement follows discovery publication, not merely key creation.
            while (!File.Exists(readyPath) || !await service.IsRunningAsync(timeout.Token).ConfigureAwait(false))
                await Task.Delay(ReadinessPollInterval, timeout.Token).ConfigureAwait(false);
            using var readyFile = new FileStream(readyPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            using var readyReader = new StreamReader(readyFile);
            var readyEndpoint = await readyReader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            var readyPid = await readyReader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            var extraLine = await readyReader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            var endpoint = await ReadEndpointAsync(root, timeout.Token).ConfigureAwait(false);
            if (readyEndpoint != endpoint || !int.TryParse(readyPid, out var pid) || pid <= 0 || extraLine is not null)
                throw new IOException($"The receiver's discovery acknowledgement does not match its persistent identity. Inspect {service.Description}.");
            if (!OperatingSystem.IsWindows())
                foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "identity")))
                    File.SetUnixFileMode(path, PrivateFileMode);
            await ProbeTunnelAsync(executable, endpoint, relayUrls, timeout.Token).ConfigureAwait(false);
            if (!await service.IsRunningAsync(timeout.Token).ConfigureAwait(false))
                throw new IOException($"The receiver exited during tunnel verification. Inspect {service.Description}.");
            return new IrohReceiverStatus(endpoint, true, service.Description);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            var localNetwork = OperatingSystem.IsMacOS()
                ? " LAN discovery and direct connections require Local Network access for dsharp Iroh (iroh-ssh) in System Settings > Privacy & Security > Local Network."
                : "";
            throw new IOException($"Receiver did not become reachable within {ReadinessTimeout.TotalSeconds} seconds. Inspect {service.Description}; check relay access and sshd.{localNetwork} The receiver was stopped; its identity is retained.", error);
        }
    }

    internal static async Task ProbeTunnelAsync(string executable, string endpoint, IReadOnlyList<string> relayUrls, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable, new[] { "proxy", endpoint }.Concat(relayUrls.SelectMany(url => new[] { "--relay-url", url })))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = StartProcess(info);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            // QUIC announces a bidirectional stream when the client first writes to it.
            await process.StandardInput.WriteAsync("SSH-2.0-dsharp-iroh-probe\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            await RequireSshBannerAsync(process.StandardOutput, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException error)
        {
            throw new IOException($"Receiver tunnel did not reach sshd: {(process.HasExited ? await stderr.ConfigureAwait(false) : error.Message)}", error);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadEndpointAsync(string root, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(root, "identity", "irohssh_ed25519")))
            return "";
        var result = await RunCommandAsync(IrohSshComponent.RequireExecutable(), ["info", "--key-dir", Path.Combine(root, "identity")], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new IOException($"Cannot read the receiver's persistent identity: {result.Output}");
        var match = EndpointPattern().Match(result.Output);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : throw new IOException($"iroh-ssh info did not return an endpoint ID: {result.Output}");
    }

    [GeneratedRegex(@"@([0-9a-fA-F]{64})(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointPattern();

    internal static async Task RequireCommandAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new IOException($"{executable} {string.Join(' ', arguments)} failed ({result.ExitCode}): {result.Output}");
    }

    internal static async Task<(int ExitCode, string Output)> RunCommandAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var info = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var process = StartProcess(info);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, $"{await stdout.ConfigureAwait(false)}{await stderr.ConfigureAwait(false)}".Trim());
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static Process StartProcess(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info) ?? throw new IOException($"Could not start {info.FileName}.");
        }
        catch (Win32Exception error)
        {
            throw new IOException($"Cannot start {info.FileName}: {error.Message}. Ensure this platform's user service tools are installed and accessible to the current user.", error);
        }
    }
}
