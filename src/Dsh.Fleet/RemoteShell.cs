using System.Diagnostics;

namespace Dsh.Fleet;

/**
 * POSIX shell fragments shared by deployment and diagnostics on a machine.
 * The harness home is resolved on the machine itself with the same rule as Dsh.Transport.LocalEndpointRoot: $DSH_HOME or ~/.dsh, redirected by a non-empty .storage-root marker.
 */
public static class RemoteShell
{
    /** Same variable as Dsh.Boot.HarnessHome.HomeEnv; it is how a configured harness home reaches the remote dsharp. */
    public const string HomeVariable = "DSH_HOME";

    /** Sets $h to the resolved harness home. */
    public const string ResolveHome =
        """h="${DSH_HOME:-$HOME/.dsh}"; if [ -s "$h/.storage-root" ]; then r=$(tr -d '\r\n' < "$h/.storage-root"); [ -n "$r" ] && h="$r"; fi; """;

    /** `DSH_HOME=<home> ` to prefix a single command, or empty when the machine uses the default home. */
    public static string HomeAssignment(string? harnessHome)
        => string.IsNullOrWhiteSpace(harnessHome) ? "" : $"{HomeVariable}={Quote(harnessHome)} ";

    /** The common start of every script run on a machine: the configured harness home. */
    public static string Prelude(MachineSpec machine)
        => string.IsNullOrWhiteSpace(machine.HarnessHome) ? "" : $"export {HomeVariable}={Quote(machine.HarnessHome)}; ";

    /** POSIX single quoting; a leading `~` or `~/` stays expandable as "$HOME". */
    public static string Quote(string value)
    {
        if (value == "~")
            return "\"$HOME\"";
        if (value.StartsWith("~/", StringComparison.Ordinal))
            return $"\"$HOME\"{Quote(value[1..])}";
        return $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    internal static Task<RemoteProcessResult> RunLocalAsync(string script, CancellationToken cancellationToken)
        => SshTransport.CaptureProcessAsync(new ProcessStartInfo("/bin/sh", ["-c", script])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }, cancellationToken);
}
