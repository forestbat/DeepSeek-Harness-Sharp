using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dsh.Pty;
using Dsh.Transport;

namespace Dsh.Tests;

/** Local authentication and request-line hardening of the PTY daemon, exercised over its real socket. */
public sealed class PtyDaemonAuthTests
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerAll = OwnerReadWrite | UnixFileMode.UserExecute;
    private const int OversizedLineBytes = 2 * 1024 * 1024;

    [Fact]
    public async Task Start_RestrictsSocketTokenAndDirectoryToOwner()
    {
        if (OperatingSystem.IsWindows())
            return;
        var root = TempTree.CreateSocketDirectory("pa");
        try
        {
            var socketPath = Path.Combine(root, "run", "dsh.sock");
            TempTree.AssertFitsMacOsSocketLimit(socketPath);
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath);
            await daemon.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(OwnerAll, File.GetUnixFileMode(Path.GetDirectoryName(socketPath)!));
            Assert.Equal(OwnerReadWrite, File.GetUnixFileMode(socketPath));
            Assert.Equal(OwnerReadWrite, File.GetUnixFileMode(LocalEndpointToken.PathFor(socketPath)));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-token")]
    public async Task Request_WithoutMatchingToken_IsRejectedAndNothingStarts(string? token)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithDaemonAsync("pr", async (socketPath, portFile) =>
        {
            var request = new PtyDaemonRequest
            {
                Method = "start",
                Token = token,
                Params = new PtyDaemonStartParams { FileName = ShellPath, Arguments = ShellArguments },
            };

            var reply = await SendRawAsync(socketPath, portFile, JsonSerializer.Serialize(request, JsonOptions), cancellationToken);

            Assert.Contains("\"ok\":false", reply);
            Assert.Contains("unauthorized", reply);
            Assert.Empty(await PtyDaemonClient.ListAsync(socketPath, portFile, cancellationToken));
        });
    }

    [Fact]
    public async Task MalformedLine_GetsErrorReply_AndDaemonKeepsServing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithDaemonAsync("pm", async (socketPath, portFile) =>
        {
            var reply = await SendRawAsync(socketPath, portFile, "{not json", cancellationToken);

            Assert.Contains("\"ok\":false", reply);
            Assert.Empty(await PtyDaemonClient.ListAsync(socketPath, portFile, cancellationToken));
        });
    }

    [Fact]
    public async Task OversizedLine_IsRefused_AndDaemonKeepsServing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithDaemonAsync("po", async (socketPath, portFile) =>
        {
            var reply = await SendRawAsync(socketPath, portFile, new string('x', OversizedLineBytes), cancellationToken);

            Assert.DoesNotContain("\"ok\":true", reply);
            Assert.Empty(await PtyDaemonClient.ListAsync(socketPath, portFile, cancellationToken));
        });
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string ShellPath => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private static List<string> ShellArguments => OperatingSystem.IsWindows() ? ["/c", "exit"] : ["-c", "exit 0"];

    private static async Task WithDaemonAsync(string prefix, Func<string, string?, Task> body)
    {
        var root = TempTree.CreateSocketDirectory(prefix);
        try
        {
            var socketPath = Path.Combine(root, "dsh.sock");
            TempTree.AssertFitsMacOsSocketLimit(socketPath);
            var portFile = OperatingSystem.IsWindows() ? Path.Combine(root, "dsh.port") : null;
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(TestContext.Current.CancellationToken);
            await body(socketPath, portFile);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** Writes one raw line to the daemon and returns whatever it answers before closing or the first newline. */
    private static async Task<string> SendRawAsync(string socketPath, string? portFile, string line, CancellationToken cancellationToken)
    {
        using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"{line}\n"), cancellationToken);
        }
        catch (IOException)
        {
            // The daemon may close an oversized line before the client finished writing it.
        }
        using var reader = new StreamReader(stream);
        try
        {
            return await reader.ReadLineAsync(cancellationToken) ?? "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static async Task<Socket> ConnectAsync(string socketPath, string? portFile, CancellationToken cancellationToken)
    {
        if (portFile is null)
        {
            var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await unix.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
            return unix;
        }
        var port = int.Parse(await File.ReadAllTextAsync(portFile, cancellationToken));
        var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        return tcp;
    }
}
