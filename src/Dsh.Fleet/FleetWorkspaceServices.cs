using Dsh.Boot;
using Dsh.RemoteHost;

namespace Dsh.Fleet;

/** Concrete machine administration and connection construction stay with the Fleet implementation. */
public sealed class FleetWorkspaceServices(MachineStore store, FleetOptions options, Func<FleetClient>? factory = null,
    BorrowedOutboundRoute? outboundRoute = null) : IWorkspaceServices, IMachineAdministration
{
    public HarnessHome? LocalHome => options.LocalHome;
    public IMachineCatalog Store => store;
    public IMachineAdministration Administration => this;
    public bool IrohAvailable => options.SshTransportAdapter is { Kind: MachineKinds.Iroh };
    public string? IrohExecutable => IrohAvailable ? options.SshTransportAdapter!.FindExecutable() : null;

    public IWorkspaceClient CreateClient()
    {
        if (factory is not null)
            return factory();
        var config = store.Load();
        return new FleetClient([MachineSpec.Local(options.LocalHome?.Root), .. config.Machines], options with { Deploy = config.Deploy });
    }

    public IWorkspaceProxyProbe CreateProxyProbe() => new FleetProxyProbe(options);

    public Task SelectBorrowedOutboundAsync(string? machine, CancellationToken cancellationToken)
        => OutboundRoute.SelectAsync(machine, cancellationToken);

    public Task<ProxyProbeResult> ProbeBorrowedOutboundAsync(string machine, Guid operationId, CancellationToken cancellationToken)
        => OutboundRoute.ProbeAsync(machine, operationId, cancellationToken);

    private BorrowedOutboundRoute OutboundRoute => outboundRoute
        ?? throw new InvalidOperationException("The workspace has no configured outbound route owner.");

    public async Task<MachineConnectionTest> TestConnectionAsync(MachineSpec machine, CancellationToken cancellationToken)
    {
        await using var connection = new MachineConnection(machine, options with { AutoReconnect = false, StartLocalHost = null });
        connection.Start();
        await connection.WaitSettledAsync(cancellationToken).ConfigureAwait(false);
        return new(connection.State, connection.Info, connection.Detail, connection.Sessions.Count);
    }

    public async Task<WorkspaceDeployment> DeployAsync(MachineSpec machine, DeploySettings deploy, bool force,
        Action<string> progress, CancellationToken cancellationToken)
    {
        var result = await new RemoteDsharpInstaller(new SshTransport(machine, options.SshTransportAdapter), options.SshTransportAdapter)
            .EnsureAsync(deploy, progress, cancellationToken, options.ArchiveFactory, force, options.LocalProxy,
                options.LocalHome ?? new HarnessHome(Path.GetDirectoryName(store.FilePath)!)).ConfigureAwait(false);
        return new(result.CommandPath, result.AlreadyPresent);
    }

    public Task<MachineSpec> EnableIrohAsync(MachineSpec machine, DeploySettings deploy, Action<MachineSpec> persist,
        Action<string> progress, CancellationToken cancellationToken)
    {
        var setup = FleetService.IrohSetup ?? throw new InvalidOperationException("The iroh plugin is not loaded.");
        return setup.EnableAndSaveAsync(machine, options with { Deploy = deploy }, persist, progress, cancellationToken);
    }

    public Task<IReadOnlyList<DoctorCheck>> DiagnoseAsync(MachineSpec machine, CancellationToken cancellationToken)
        => FleetDoctor.CheckAsync(machine, options, cancellationToken);


}
