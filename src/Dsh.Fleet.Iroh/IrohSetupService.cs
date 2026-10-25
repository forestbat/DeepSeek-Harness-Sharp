namespace Dsh.Fleet.Iroh;

internal sealed class IrohSetupService : IFleetIrohSetup
{
    public bool IsAvailable => IrohSshComponent.FindExecutable() is not null;

    public Task<MachineSpec> EnableAndSaveAsync(
        MachineSpec machine,
        FleetOptions options,
        Action<MachineSpec> persist,
        Action<string>? progress,
        CancellationToken cancellationToken)
        => IrohRemoteInstaller.EnableAndSaveAsync(machine, options, persist, progress, cancellationToken);
}
