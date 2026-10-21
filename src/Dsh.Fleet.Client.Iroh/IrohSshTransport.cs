using System.Text;

namespace Dsh.Fleet.Iroh;

/** The optional iroh SSH proxy adapter shared by command execution, deployment and Host connections. */
public sealed class IrohSshTransport : ISshTransportAdapter
{
    public string Kind => MachineKinds.Iroh;

    public bool IsAvailable => IrohSshComponent.FindExecutable() is not null;

    public SshTransportConfiguration Configure(MachineSpec machine)
    {
        machine.ValidateTransportSettings();
        if (!machine.IsIroh)
            throw new ArgumentException("The iroh SSH adapter requires an iroh machine.", nameof(machine));
        var endpoint = machine.EndpointId!.ToLowerInvariant();
        var command = new StringBuilder(QuoteArgument(IrohSshComponent.RequireExecutable()));
        command.Append(" proxy ").Append(endpoint);
        foreach (var relay in machine.RelayUrls ?? [])
            command.Append(" --relay-url ").Append(QuoteArgument(new Uri(relay).AbsoluteUri));
        return new SshTransportConfiguration(command.ToString(), $"iroh-{endpoint}", true);
    }

    private static string QuoteArgument(string value)
    {
        var literal = value.Replace("%", "%%", StringComparison.Ordinal);
        return OperatingSystem.IsWindows()
            ? $"\"{literal}\""
            : RemoteShell.Quote(literal);
    }
}
