using Dsh.Boot;

namespace Dsh.Fleet;

/**
 * The machine list and deployment preferences shared by the GUI, the CLI and the web bridge: the `fleet:` section of settings.yaml.
 * settings.yaml is owner-only, so the SSH passwords of password authentication stay private.
 * Reading never creates settings.yaml; saving rewrites only the fleet section and keeps comments, the other sections and the file's line endings.
 * Only settings.yaml is read or written; configurations from experimental layouts must be added explicitly.
 */
public sealed class MachineStore(HarnessHome home)
{
    public string FilePath => home.SettingsFile;

    public FleetConfig Load()
    {
        return File.Exists(FilePath) && HarnessSettings.Load(home).Fleet is { } fleet
            ? FromSettings(fleet)
            : FleetConfig.Empty;
    }

    /** The current document and the fleet mutation share the settings publication transaction. */
    public void Save(FleetConfig config)
    {
        Validate(config.Machines);
        HarnessSettings.Update(home, settings =>
        {
            settings.Fleet = ToSettings(config);
            return true;
        });
    }

    /** Adds a machine, or replaces the one with the same name. */
    public FleetConfig Upsert(MachineSpec machine)
    {
        FleetConfig updated = FleetConfig.Empty;
        HarnessSettings.Update(home, settings =>
        {
            var config = settings.Fleet is { } fleet ? FromSettings(fleet) : FleetConfig.Empty;
            var machines = config.Machines.Where(existing => !string.Equals(existing.Name, machine.Name, StringComparison.Ordinal)).Append(machine).ToList();
            updated = config with { Machines = machines };
            Validate(updated.Machines);
            settings.Fleet = ToSettings(updated);
            return true;
        });
        return updated;
    }

    /** Removes a machine; returns false when no machine has that name. */
    public bool Remove(string name)
    {
        if (!File.Exists(FilePath))
            return false;
        return HarnessSettings.Update(home, settings =>
        {
            var config = settings.Fleet is { } fleet ? FromSettings(fleet) : FleetConfig.Empty;
            var machines = config.Machines.Where(existing => !string.Equals(existing.Name, name, StringComparison.Ordinal)).ToList();
            if (machines.Count == config.Machines.Count)
                return false;
            settings.Fleet = ToSettings(config with { Machines = machines });
            return true;
        });
    }

    private static FleetConfig FromSettings(FleetSettings fleet)
    {
        var config = new FleetConfig([.. fleet.Machines.Select(FromSettings)], FromSettings(fleet.Deploy));
        foreach (var machine in config.Machines)
            ValidateOutboundTunnel(machine);
        return config;
    }

    private static MachineSpec FromSettings(MachineSettings machine)
        => new(
            machine.Name ?? "",
            machine.Kind ?? MachineKinds.Ssh,
            machine.Host ?? "",
            machine.Port ?? MachineSpec.DefaultSshPort,
            machine.User ?? "",
            machine.Auth ?? SshAuth.Agent,
            machine.KeyPath,
            machine.Proxy,
            machine.RemotePath,
            machine.Password,
            machine.HarnessHome,
            machine.SshOptions,
            machine.EndpointId,
            machine.RelayUrls,
            machine.OutboundTunnel is { } tunnel ? new OutboundTunnelSpec(tunnel.Mode ?? "", tunnel.ProxyUrl ?? "", tunnel.LocalPort) : null,
            machine.IrohExecutablePath,
            machine.IrohArguments);

    private static DeploySettings FromSettings(FleetDeploySettings? deploy)
    {
        var defaults = new DeploySettings();
        if (deploy is null)
            return defaults;
        return new DeploySettings
        {
            AutoDeploy = deploy.AutoDeploy ?? defaults.AutoDeploy,
            ReleaseBaseUrl = deploy.ReleaseBaseUrl ?? defaults.ReleaseBaseUrl,
            ReleaseVersion = deploy.ReleaseVersion ?? defaults.ReleaseVersion,
            Via = deploy.Via ?? defaults.Via,
            ServerProxy = deploy.ServerProxy,
        };
    }

    /** Defaults are left out, so the section only shows what the user chose; an empty list with default preferences removes the section. */
    private static FleetSettings? ToSettings(FleetConfig config)
    {
        if (config.Machines.Count == 0 && config.Deploy == new DeploySettings())
            return null;
        return new FleetSettings { Machines = [.. config.Machines.Select(ToSettings)], Deploy = ToSettings(config.Deploy) };
    }

    private static MachineSettings ToSettings(MachineSpec machine)
    {
        var ssh = !machine.IsLocal;
        return new MachineSettings
        {
            Name = machine.Name,
            Kind = machine.Kind,
            Host = ssh ? machine.Host : null,
            Port = ssh && machine.Port != MachineSpec.DefaultSshPort ? machine.Port : null,
            User = ssh && machine.User.Length > 0 ? machine.User : null,
            Auth = ssh ? machine.Auth : null,
            KeyPath = machine.KeyPath,
            Proxy = machine.Proxy,
            RemotePath = machine.RemotePath,
            Password = machine.Password,
            HarnessHome = machine.HarnessHome,
            SshOptions = machine.SshOptions is { Count: > 0 } options ? [.. options] : null,
            EndpointId = machine.EndpointId,
            RelayUrls = machine.RelayUrls is { Count: > 0 } relays ? [.. relays] : null,
            IrohExecutablePath = machine.IrohExecutablePath,
            IrohArguments = machine.IrohArguments,
            OutboundTunnel = machine.OutboundTunnel is { } tunnel
                ? new OutboundTunnelSettings { Mode = tunnel.Mode, ProxyUrl = tunnel.ProxyUrl, LocalPort = tunnel.LocalPort }
                : null,
        };
    }

    private static FleetDeploySettings? ToSettings(DeploySettings deploy)
    {
        var defaults = new DeploySettings();
        if (deploy == defaults)
            return null;
        return new FleetDeploySettings
        {
            AutoDeploy = deploy.AutoDeploy == defaults.AutoDeploy ? null : deploy.AutoDeploy,
            ReleaseBaseUrl = deploy.ReleaseBaseUrl == defaults.ReleaseBaseUrl ? null : deploy.ReleaseBaseUrl,
            ReleaseVersion = deploy.ReleaseVersion == defaults.ReleaseVersion ? null : deploy.ReleaseVersion,
            Via = deploy.Via == defaults.Via ? null : deploy.Via,
            ServerProxy = deploy.ServerProxy,
        };
    }

    public static void Validate(IReadOnlyList<MachineSpec> machines)
    {
        var duplicate = machines.GroupBy(machine => machine.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"machine name \"{duplicate.Key}\" is used more than once");
        var invalid = machines.FirstOrDefault(machine => string.IsNullOrWhiteSpace(machine.Name) || machine.Name == MachineSpec.LocalName || machine.Name.Contains('/'));
        if (invalid is not null)
            throw new InvalidOperationException($"machine name \"{invalid.Name}\" is reserved or invalid (it must be non-empty, not \"{MachineSpec.LocalName}\", and contain no '/')");
        var incomplete = machines.FirstOrDefault(machine => machine.Kind == MachineKinds.Ssh && string.IsNullOrWhiteSpace(machine.Host));
        if (incomplete is not null)
            throw new InvalidOperationException($"SSH machine \"{incomplete.Name}\" has no host");
        var noUser = machines.FirstOrDefault(machine => !machine.IsLocal && string.IsNullOrWhiteSpace(machine.User));
        if (noUser is not null)
            throw new InvalidOperationException($"remote machine \"{noUser.Name}\" has no SSH user");
        var badAuth = machines.FirstOrDefault(machine => !machine.IsLocal && machine.Auth is not (SshAuth.Agent or SshAuth.Key or SshAuth.Password));
        if (badAuth is not null)
            throw new InvalidOperationException($"remote machine \"{badAuth.Name}\" has an invalid auth mode");
        var noKey = machines.FirstOrDefault(machine => !machine.IsLocal && machine.Auth == SshAuth.Key && string.IsNullOrWhiteSpace(machine.KeyPath));
        if (noKey is not null)
            throw new InvalidOperationException($"SSH machine \"{noKey.Name}\" uses key auth without a key path");
        var noPassword = machines.FirstOrDefault(machine => !machine.IsLocal && machine.Auth == SshAuth.Password && string.IsNullOrEmpty(machine.Password));
        if (noPassword is not null)
            throw new InvalidOperationException($"SSH machine \"{noPassword.Name}\" uses password auth without a password");
        foreach (var machine in machines)
        {
            machine.ValidateTransportSettings();
            ValidateOutboundTunnel(machine);
        }
    }

    /** Validates without exposing credentials and preserves the proxy's logical hostname and user information. */
    public static Uri? ValidateOutboundTunnel(MachineSpec machine)
    {
        if (machine.OutboundTunnel is not { } tunnel)
            return null;
        if (machine.IsLocal)
            throw new InvalidOperationException("outbound tunnels are not available for local machines");
        if (machine.Kind is not (MachineKinds.Ssh or MachineKinds.Iroh))
            throw new InvalidOperationException("outbound tunnels require an SSH or iroh machine");
        if (string.IsNullOrWhiteSpace(tunnel.Mode) || string.IsNullOrWhiteSpace(tunnel.ProxyUrl))
            throw new InvalidOperationException("outboundTunnel mode and proxyUrl must both be supplied");
        if (tunnel.Mode is not ("proxyCommand" or "localForward"))
            throw new InvalidOperationException("outboundTunnel mode must be proxyCommand or localForward");
        if (tunnel.LocalPort is < 1 or > 65535)
            throw new InvalidOperationException("outboundTunnel localPort must be between 1 and 65535");
        if (tunnel.Mode == "proxyCommand" && tunnel.LocalPort is not null)
            throw new InvalidOperationException("outboundTunnel proxyCommand must not specify localPort");
        var url = tunnel.ProxyUrl;
        if (!url.Contains("://", StringComparison.Ordinal) || url.Any(char.IsWhiteSpace) || url.Contains('\\')
            || !Uri.TryCreate(url, UriKind.Absolute, out var proxy) || proxy.Host.Length == 0
            || proxy.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")
            || proxy.Port is < 1 or > 65535
            || proxy.AbsolutePath is not ("" or "/") || proxy.Query.Length > 0 || proxy.Fragment.Length > 0)
            throw new InvalidOperationException("outboundTunnel proxyUrl requires an HTTP(S) or SOCKS4/4a/5 proxy hostname and valid port, without a path, query or fragment");
        var authority = url.AsSpan(url.IndexOf("://", StringComparison.Ordinal) + 3);
        var end = authority.IndexOfAny('/', '?', '#');
        if (end >= 0)
            authority = authority[..end];
        if (authority.EndsWith(":"))
            throw new InvalidOperationException("outboundTunnel proxyUrl must not contain an empty port");
        return proxy;
    }
}
