using System.Text;

namespace Dsh.Fleet.Iroh;

/** The optional iroh SSH proxy adapter shared by command execution, deployment and Host connections. */
public sealed class IrohSshTransport : ISshTransportAdapter
{
    public string Kind => MachineKinds.Iroh;

    public bool IsAvailable => IrohSshComponent.FindExecutable() is not null;

    public string? FindExecutable() => IrohSshComponent.FindExecutable();

    public SshTransportConfiguration Configure(MachineSpec machine)
    {
        machine.ValidateTransportSettings();
        if (!machine.IsIroh)
            throw new ArgumentException("The iroh SSH adapter requires an iroh machine.", nameof(machine));
        var endpoint = machine.EndpointId!.ToLowerInvariant();
        var executable = string.IsNullOrWhiteSpace(machine.IrohExecutablePath)
            ? IrohSshComponent.RequireExecutable()
            : ExpandHome(machine.IrohExecutablePath.Trim());
        if (!File.Exists(executable))
            throw new FileNotFoundException($"iroh-ssh executable was not found at {executable}", executable);
        var command = new StringBuilder(QuoteArgument(executable));
        command.Append(" proxy ").Append(endpoint);
        foreach (var relay in machine.RelayUrls ?? [])
            command.Append(" --relay-url ").Append(QuoteArgument(new Uri(relay).AbsoluteUri));
        if (!string.IsNullOrWhiteSpace(machine.IrohArguments))
            command.Append(' ').Append(machine.IrohArguments.Trim());
        return new SshTransportConfiguration(command.ToString(), $"iroh-{endpoint}", true);
    }

    private static string ExpandHome(string path)
    {
        var expanded = path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 1 ? path[2..] : "")
            : path;
        return Path.GetFullPath(expanded);
    }

    private static string QuoteArgument(string value)
    {
        var literal = value.Replace("%", "%%", StringComparison.Ordinal);
        return OperatingSystem.IsWindows()
            ? $"\"{literal}\""
            : RemoteShell.Quote(literal);
    }
}
