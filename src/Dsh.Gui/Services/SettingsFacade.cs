using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Runtime;

namespace Dsh.Gui.Services;

/** Shared settings-file mutations without a slash command; session commands run through HostChatSession. */
public sealed class SettingsFacade(Context ctx, HarnessHome home, FleetWorkspace? workspace = null)
{
    private string?[] _networkSecrets = [];

    public event Action? Changed;

    public string SettingsPath => Path.Combine(home.Root, "settings.yaml");

    public HarnessSettings Load()
    {
        var settings = HarnessSettings.Load(home);
        var secrets = new List<string?> { settings.Network?.OutboundProxy?.Url, settings.Network?.OutboundProxy?.ProbeUrl };
        foreach (var machine in settings.Fleet?.Machines ?? [])
        {
            secrets.Add(machine.Password);
            secrets.Add(machine.Proxy);
            secrets.Add(machine.OutboundTunnel?.ProxyUrl);
        }
        _networkSecrets = [.. secrets];
        return settings;
    }

    public void UpdateSafety(bool autoApprove, IReadOnlyList<string> blacklist)
    {
        var settings = Load();
        settings.Safety = new SafetySettings
        {
            AutoApprove = autoApprove,
            Blacklist = [.. blacklist],
        };
        settings.Save(home);
        Changed?.Invoke();
    }

    /** Persist the baseline without changing the explicit borrowed selection. */
    public void UpdateOutboundProxy(OutboundProxySettings proxy)
    {
        HarnessSettings.SaveOutboundProxy(home, proxy, OutboundProxy.FromContext(ctx));
        Changed?.Invoke();
    }

    public async Task UpdateOutboundProxyAsync(OutboundProxySettings proxy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateOutboundProxy(proxy);
        await ReloadLocalHostsAsync(cancellationToken);
    }

    public IReadOnlyList<string> BorrowedOutboundMachines()
        => new MachineStore(home).Load().Machines
            .Where(machine => !machine.IsLocal && machine.OutboundTunnel is not null)
            .Select(machine => machine.Name).Order(StringComparer.Ordinal).ToArray();

    internal string RedactNetworkError(string detail) => OutboundProxy.Redact(detail, _networkSecrets);

    public FleetProxyProbe CreateProxyProbe() => new(workspace?.Options ?? new FleetOptions
    {
        LocalHome = home,
        LocalProxy = OutboundProxy.FromContext(ctx),
        SshTransportAdapter = ctx.Get<FleetService>(FleetService.ServiceName, false)?.SshTransportAdapter,
    });

    public async Task SelectBorrowedOutboundAsync(string? machine, CancellationToken cancellationToken)
    {
        await OutboundRoute.SelectAsync(machine, cancellationToken);
        Changed?.Invoke();
        await ReloadLocalHostsAsync(cancellationToken);
    }

    public Task<ProxyProbeResult> ProbeBorrowedOutboundAsync(string machine, Guid operationId, CancellationToken cancellationToken)
        => OutboundRoute.ProbeAsync(machine, operationId, cancellationToken);

    private BorrowedOutboundRoute OutboundRoute
        => ctx.Get<FleetService>(FleetService.ServiceName, false)?.OutboundRoute
            ?? throw new InvalidOperationException("The Fleet outbound route service is unavailable.");

    private Task ReloadLocalHostsAsync(CancellationToken cancellationToken)
        => workspace?.ReloadLocalOutboundAsync(home.Root, cancellationToken) ?? Task.CompletedTask;

    /** 某个插件的参数段(例如 GUI 自己的外观/显卡设置); 合并已有键后写回。 */
    public void SavePluginParameters(string package, IReadOnlyDictionary<string, object?> parameters)
    {
        var settings = Load();
        var existing = settings.Plugins.GetValueOrDefault(package);
        var merged = new Dictionary<string, object?>(existing?.Parameters ?? [], StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
            merged[key] = value;
        settings.Plugins[package] = (existing ?? new PluginSetting()) with { Parameters = merged };
        settings.SavePlugins(home);
        Changed?.Invoke();
    }

}
