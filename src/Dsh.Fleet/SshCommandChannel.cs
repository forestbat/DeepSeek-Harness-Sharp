using System.Diagnostics;
using Dsh.Transport;

namespace Dsh.Fleet;

/** Internal process lease for the host handshake; never exposed as a second raw web/relay API. */
internal sealed class SshCommandChannel : IDisposable
{
    private string? _askPass;
    private readonly Func<string> _errorOutput;
    private int _disposed;

    public SshCommandChannel(Process process, string? askPass, Func<string> errorOutput)
    {
        Process = process;
        _askPass = askPass;
        _errorOutput = errorOutput;
        Stream = new StandardIoDuplexStream(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
    }

    public Process Process { get; }
    public Stream Stream { get; }
    public string ErrorOutput => _errorOutput();

    public void AuthenticationCompleted()
    {
        if (Interlocked.Exchange(ref _askPass, null) is { } path)
            SshTransport.TryDelete(path);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            Stream.Dispose();
        }
        finally
        {
            SshTransport.Kill(Process);
            AuthenticationCompleted();
        }
    }
}
