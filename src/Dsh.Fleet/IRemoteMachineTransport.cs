namespace Dsh.Fleet;

/** Command and upload boundary consumed by deployment and diagnostics, independent of OpenSSH processes. */
public interface IRemoteMachineTransport
{
    MachineSpec Machine { get; }
    Task<RemoteProcessResult> CaptureAsync(string command, CancellationToken cancellationToken);
    Task<string> RunAsync(string command, CancellationToken cancellationToken);
    Task UploadAsync(string localPath, string remotePath, CancellationToken cancellationToken);
    string CommandLine(string remoteCommand);
}

/** Transport failures are distinguished from remote command failures by the transport, not deployment policy. */
public sealed record RemoteProcessResult(int ExitCode, string StdOut, string StdErr, bool TransportFailed = false)
{
    public string FailureDetail => string.IsNullOrWhiteSpace(StdErr) ? "(无 stderr)" : StdErr.Trim();
}
