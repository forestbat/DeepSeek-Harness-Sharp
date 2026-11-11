namespace Dsh.Transport;

/// <summary>Controls this Context's shared outbound selection without exposing its transport implementation.</summary>
public interface IOutboundRouteController
{
    const string ServiceName = "outboundRouteController";

    Task SelectAsync(string? machine, CancellationToken cancellationToken);
    Task ReloadAsync(CancellationToken cancellationToken);
}
