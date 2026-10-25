namespace Dsh.Fleet;

public interface IFleetIrohSetup
{
    bool IsAvailable { get; }

    Task<MachineSpec> EnableAndSaveAsync(
        MachineSpec machine,
        FleetOptions options,
        Action<MachineSpec> persist,
        Action<string>? progress,
        CancellationToken cancellationToken);
}
