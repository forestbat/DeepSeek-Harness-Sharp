using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Dsh.Pty;

public static class PtyDaemonClient
{
    public static Task<IReadOnlyList<PtyDaemonSessionDto>> ListAsync(CancellationToken cancellationToken = default)
        => ListAsync(PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static async Task<IReadOnlyList<PtyDaemonSessionDto>> ListAsync(string socketPath, string? portFile, CancellationToken cancellationToken = default)
    {
        using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
        using var stream = new NetworkStream(socket, ownsSocket: true);
        await WriteRequestAsync(stream, new PtyDaemonRequest { Method = "list" }, cancellationToken);
        var response = await ReadResponseAsync(stream, cancellationToken);
        return response.Sessions ?? [];
    }

    public static Task<PtyDaemonSessionDto> StartAsync(PtyDaemonStartParams parameters, CancellationToken cancellationToken = default)
        => StartAsync(parameters, PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static async Task<PtyDaemonSessionDto> StartAsync(PtyDaemonStartParams parameters, string socketPath, string? portFile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
        using var stream = new NetworkStream(socket, ownsSocket: true);
        await WriteRequestAsync(stream, new PtyDaemonRequest { Method = "start", Params = parameters }, cancellationToken);
        var response = await ReadResponseAsync(stream, cancellationToken);
        return response.Session
            ?? throw new InvalidOperationException("daemon returned no PTY session");
    }

    public static Task AttachAsync(
        string id,
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
        => AttachAsync(id, input, output, PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static async Task AttachAsync(
        string id,
        Stream input,
        Stream output,
        string socketPath,
        string? portFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
        using var stream = new NetworkStream(socket, ownsSocket: true);
        await WriteRequestAsync(stream, new PtyDaemonRequest { Method = "attach", Id = id }, cancellationToken);
        var response = await ReadResponseAsync(stream, cancellationToken);
        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "daemon attach failed");

        using var raw = PtyRawMode.TryEnable();
        await TunnelAsync(input, output, stream, cancellationToken);
    }

    public static Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
        => IsRunningAsync(PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static async Task<bool> IsRunningAsync(string socketPath, string? portFile, CancellationToken cancellationToken = default)
    {
        try
        {
            using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
            return true;
        }
        catch (PtyDaemonNotRunningException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (await IsRunningAsync(cancellationToken))
            return;

        StartDaemonProcess();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsRunningAsync(cancellationToken))
                return;
            await Task.Delay(100, cancellationToken);
        }

        if (await IsRunningAsync(cancellationToken))
            return;
        throw new InvalidOperationException("dsh tui daemon failed to start");
    }

    private static void StartDaemonProcess()
    {
        var executable = Environment.ProcessPath ?? "dotnet";
        var assembly = Environment.GetCommandLineArgs()[0];
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add("tui");
        startInfo.ArgumentList.Add("daemon");
        var process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("failed to start dsh tui daemon");
        _ = process;
    }

    private static async Task<Socket> ConnectAsync(string socketPath, string? portFile, CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (portFile is null)
                    throw new PtyDaemonNotRunningException();
                if (!File.Exists(portFile))
                    throw new PtyDaemonNotRunningException();
                var port = int.Parse(await File.ReadAllTextAsync(portFile, cancellationToken));
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return client.Client;
            }

            if (!File.Exists(socketPath))
                throw new PtyDaemonNotRunningException();
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            return socket;
        }
        catch (SocketException)
        {
            throw new PtyDaemonNotRunningException();
        }
        catch (IOException)
        {
            throw new PtyDaemonNotRunningException();
        }
    }

    private static async Task WriteRequestAsync(Stream stream, PtyDaemonRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request, PtyDaemonJson.Options) + "\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<PtyDaemonResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = await ReadLineAsync(stream, cancellationToken)
            ?? throw new IOException("daemon closed the connection");
        var response = JsonSerializer.Deserialize<PtyDaemonResponse>(line, PtyDaemonJson.Options)
            ?? throw new InvalidOperationException("daemon returned an invalid response");
        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "daemon request failed");
        return response;
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single, cancellationToken);
            if (read == 0)
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (single[0] == (byte)'\n')
                return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(single[0]);
        }
    }

    private static async Task TunnelAsync(Stream input, Stream output, NetworkStream stream, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumpInput = PumpInputAsync(input, stream, linked.Token);
        var pumpOutput = PumpOutputAsync(output, stream, linked.Token);
        await Task.WhenAny(pumpInput, pumpOutput);
        linked.Cancel();

        try
        {
            await pumpInput;
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await pumpOutput;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task PumpInputAsync(Stream input, NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return;
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private static async Task PumpOutputAsync(Stream output, NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }
}