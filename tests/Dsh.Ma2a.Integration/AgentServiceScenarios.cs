using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Integration;

internal static class AgentServiceScenarios
{
    private const string AgentName = "native-example";
    private const string ServiceName = "native-example";

    public static async Task RunAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        await ShellAsync(pair, cancellationToken);
        await ProxyAsync(pair, cancellationToken);
        await AgentAsync(pair, cancellationToken);
        await HostedServiceAsync(pair, cancellationToken);
    }

    private static string RequestId() => Guid.NewGuid().ToString("N");

    private static async Task ShellAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        await pair.ExposeAsync("shell", cancellationToken: cancellationToken);
        var program = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
        string[] args = OperatingSystem.IsWindows()
            ? ["-NoLogo", "-NoProfile", "-Command", "Write-Output 'ma2a-native-shell'; [Console]::ReadLine()"]
            : ["-c", "printf ma2a-native-shell; read ignored"];
        await using (var session = await Ma2aServiceSession.OpenAsync(pair.Caller, pair.TargetId, "shell", RequestId(),
            Ma2aServiceRequests.Shell(program, args), cancellationToken))
        {
            NativeRuntimePair.Check((await session.ReadAsync(cancellationToken)).Kind == Ma2aFrameKind.Accept, "Shell accepts a real PTY program.");
            await session.ControlAsync(Ma2aServiceRequests.Resize(100, 30), cancellationToken);
            await ReadShellMarkerAsync(session, cancellationToken);
            await session.CancelAsync(cancellationToken);
            var outcome = await ReadTerminalAsync(session, cancellationToken);
            NativeRuntimePair.Check(outcome?["1"]?[1]?.GetValue<ulong>() == 2, "Shell cancellation terminates with the cancelled reason.");
        }
        await NativeRuntimePair.PassedAsync("native Shell real PTY output, resize and explicit cancellation");
        await using (var session = await Ma2aServiceSession.OpenAsync(pair.Caller, pair.TargetId, "shell", RequestId(),
            Ma2aServiceRequests.Shell(program, args), cancellationToken))
        {
            NativeRuntimePair.Check((await session.ReadAsync(cancellationToken)).Kind == Ma2aFrameKind.Accept, "Second Shell is admitted before withdrawal.");
            await ReadShellMarkerAsync(session, cancellationToken);
            await pair.GrantAsync(false, cancellationToken);
            var outcome = await ReadTerminalAsync(session, cancellationToken);
            NativeRuntimePair.Check(outcome?["1"]?[1]?.GetValue<ulong>() == 3, "A running Shell ends with shutdown after its grant is withdrawn.");
        }
        await pair.GrantAsync(true, cancellationToken);
        await pair.SyncAsync(cancellationToken);
        await NativeRuntimePair.PassedAsync("running native Shell is stopped by authorization withdrawal");
    }

    private static async Task ReadShellMarkerAsync(Ma2aServiceSession session, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        while (output.Length < 65_536)
        {
            var frame = await session.ReadAsync(cancellationToken);
            NativeRuntimePair.Check(frame.Kind == Ma2aFrameKind.Data && frame.Channel == 1, "Shell emits PTY output before ending.");
            await output.WriteAsync(frame.Data, cancellationToken);
            if (Encoding.UTF8.GetString(output.ToArray()).Contains("ma2a-native-shell", StringComparison.Ordinal)) return;
        }
        throw new InvalidDataException("Shell output exceeded the marker search bound.");
    }

    private static async Task<JsonNode?> ReadTerminalAsync(Ma2aServiceSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await session.ReadAsync(cancellationToken);
            if (frame.Kind == Ma2aFrameKind.Outcome) return frame.DecodeCbor();
        }
    }

    private static async Task ProxyAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await pair.ExposeAsync("proxy", new JsonObject { ["host"] = "127.0.0.1", ["port_start"] = port, ["port_end"] = port }, cancellationToken);
        var content = new byte[] { 0, 1, 255, 128, 10, 13 };
        var destination = EchoDestinationAsync(listener, content, cancellationToken);
        var (_, output) = await ServicesScenarios.RunAsync(pair, "proxy",
            Ma2aServiceRequests.Proxy("127.0.0.1", (ushort)port), content, cancellationToken);
        await destination;
        NativeRuntimePair.Check(output.SequenceEqual(content), "Native Proxy preserves binary bytes through TCP half-close.");
        await NativeRuntimePair.PassedAsync("native Proxy real TCP endpoint, binary bytes and half-close");
    }

    private static async Task EchoDestinationAsync(TcpListener listener, byte[] expected, CancellationToken cancellationToken)
    {
        using var peer = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = peer.GetStream();
        var received = new byte[expected.Length];
        await stream.ReadExactlyAsync(received, cancellationToken);
        NativeRuntimePair.Check(received.SequenceEqual(expected), "TCP peer receives the exact Proxy input.");
        var probe = new byte[1];
        NativeRuntimePair.Check(await stream.ReadAsync(probe, cancellationToken) == 0, "Proxy End performs TCP write shutdown.");
        await stream.WriteAsync(received, cancellationToken);
        peer.Client.Shutdown(SocketShutdown.Send);
    }

    internal static string ExampleBinary(NativeRuntimePair pair, string name)
    {
        var path = Path.Combine(Path.GetDirectoryName(pair.BinaryPath)!, OperatingSystem.IsWindows() ? $"{name}.exe" : name);
        NativeRuntimePair.Check(File.Exists(path), "Build the MA2A SDK example binaries next to the selected Runtime before acceptance.");
        return path;
    }

    private static async Task AgentAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var define = NativeRuntimePair.Mutation("agent_define");
        define["agent"] = AgentName;
        define["description"] = "Native SDK integration acceptance";
        define["program"] = ExampleBinary(pair, "ma2a-example-agent");
        define["args"] = new JsonArray();
        define["cwd"] = null;
        define["env"] = new JsonArray();
        define["tools"] = new JsonArray("transfer");
        define["max_tasks"] = 4;
        await pair.Target.CallAsync(define, cancellationToken);
        var start = NativeRuntimePair.Mutation("agent_start");
        start["agent"] = AgentName;
        await pair.Target.CallAsync(start, cancellationToken);
        await pair.ExposeAsync("agent", new JsonObject { ["agent"] = AgentName }, cancellationToken);
        var (cards, _) = await ServicesScenarios.RunAsync(pair, "agent", Ma2aServiceRequests.Agents(), null, cancellationToken);
        NativeRuntimePair.Check(cards!.ToJsonString().Contains(AgentName, StringComparison.Ordinal), "Native Agent discovery returns the exposed definition.");
        var task = await SubmitAsync(pair, "echo ma2a-native-agent", cancellationToken);
        await WatchAsync(pair, task, 3, cancellationToken);
        var (details, _) = await ServicesScenarios.RunAsync(pair, "agent", Ma2aServiceRequests.AgentGet(AgentName, task), null, cancellationToken);
        NativeRuntimePair.Check(details?["1"]?[1]?[2]?[1]?[0]?[1]?.GetValue<string>() == "ma2a-native-agent", "Agent Get returns the actual SDK task result.");
        var conversation = await SubmitAsync(pair, "ask question", cancellationToken);
        await ServicesScenarios.RunAsync(pair, "agent", Ma2aServiceRequests.AgentSend(AgentName, conversation, "answer"), null, cancellationToken);
        await WatchAsync(pair, conversation, 3, cancellationToken);
        var (continued, _) = await ServicesScenarios.RunAsync(pair, "agent", Ma2aServiceRequests.AgentGet(AgentName, conversation), null, cancellationToken);
        NativeRuntimePair.Check(continued?["1"]?[1]?[2]?[1]?[0]?[1]?.GetValue<string>() == "question answer", "Native continuation delivers another message to the same task.");
        var sleeping = await SubmitAsync(pair, "sleep 60000", cancellationToken);
        var (cancelled, _) = await ServicesScenarios.RunAsync(pair, "agent", Ma2aServiceRequests.AgentCancel(AgentName, sleeping), null, cancellationToken);
        NativeRuntimePair.Check(cancelled?["1"]?[1]?[5]?.GetValue<ulong>() == 5, "Remote task cancellation waits for the terminal cancelled task.");
        await WatchAsync(pair, sleeping, 5, cancellationToken);
        await AgentArtifactAsync(pair, cancellationToken);
        var stop = NativeRuntimePair.Mutation("agent_stop");
        stop["agent"] = AgentName;
        await pair.Target.CallAsync(stop, cancellationToken);
        await NativeRuntimePair.PassedAsync("native reference SDK Agent discovery/submit/get/continuation/watch/cancel/artifact integrity");
    }

    private static async Task<string> SubmitAsync(NativeRuntimePair pair, string text, CancellationToken cancellationToken)
    {
        var (result, _) = await ServicesScenarios.RunAsync(pair, "agent", Ma2aServiceRequests.AgentSubmit(AgentName, text), null, cancellationToken);
        return result?["1"]?[1]?[0]?["$bytes"]?.GetValue<string>() ?? throw new InvalidDataException("Agent submit has no task identifier.");
    }

    private static async Task WatchAsync(NativeRuntimePair pair, string task, ulong state, CancellationToken cancellationToken)
    {
        await using var session = await Ma2aServiceSession.OpenAsync(pair.Caller, pair.TargetId, "agent", RequestId(),
            Ma2aServiceRequests.AgentWatch(AgentName, task), cancellationToken);
        var events = 0;
        while (true)
        {
            var frame = await session.ReadAsync(cancellationToken);
            if (frame.Kind == Ma2aFrameKind.Data)
            {
                NativeRuntimePair.Check(frame.Channel == 1 && Ma2aCbor.Decode(frame.Data) is JsonArray, "Agent events are structured CBOR.");
                events++;
            }
            if (frame.Kind != Ma2aFrameKind.Outcome) continue;
            NativeRuntimePair.Check(events > 0 && frame.DecodeCbor()?["1"]?[1]?[5]?.GetValue<ulong>() == state, "Watch delivers retained events and the expected terminal task state.");
            return;
        }
    }

    private static async Task AgentArtifactAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var source = Path.Combine(pair.Root, "artifact-source");
        Directory.CreateDirectory(source);
        var content = Encoding.UTF8.GetBytes("MA2A native artifact content\n");
        await File.WriteAllBytesAsync(Path.Combine(source, "input.txt"), content, cancellationToken);
        var exposure = NativeRuntimePair.Mutation("service_expose");
        exposure["space_id"] = pair.SpaceId;
        exposure["kind"] = "files_root";
        foreach (var field in new[] { "root", "path", "access", "host", "port_start", "port_end", "agent", "service" }) exposure[field] = null;
        exposure["root"] = "artifact";
        exposure["path"] = source;
        exposure["access"] = "read_only";
        await pair.Caller.CallAsync(exposure, cancellationToken);
        var grant = NativeRuntimePair.Mutation("space_grant");
        grant["space_id"] = pair.SpaceId;
        grant["peer_endpoint_id"] = pair.TargetId;
        foreach (var capability in new[] { "files_read", "files_write", "exec", "shell", "proxy", "process", "agent", "service" }) grant[capability] = true;
        await pair.Target.CallAsync(grant, cancellationToken);
        await pair.SyncAsync(cancellationToken);
        var task = await SubmitAsync(pair, $"fetch {pair.CallerId} artifact /input.txt result.txt", cancellationToken);
        await WatchAsync(pair, task, 3, cancellationToken);
        var destination = Path.Combine(pair.Root, "artifact-result.txt");
        await Ma2aFileTransfer.DownloadArtifactAsync(pair.Caller, pair.TargetId, AgentName, task, "result.txt", destination,
            RequestId(), cancellationToken: cancellationToken);
        NativeRuntimePair.Check((await File.ReadAllBytesAsync(destination, cancellationToken)).SequenceEqual(content), "Native Agent artifact download returns the verified published content.");
    }

    private static Task HostedServiceAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
        => WithHostedServiceAsync(pair, async (_, token) =>
        {
            var (descriptors, _) = await ServicesScenarios.RunAsync(pair, "service", Ma2aServiceRequests.Describe(), null, token);
            NativeRuntimePair.Check(descriptors!.ToJsonString().Contains(ServiceName, StringComparison.Ordinal), "Native service discovery contains the exposed hosted service.");
            var (echo, _) = await ServicesScenarios.RunAsync(pair, "service", Ma2aServiceRequests.ServiceCall(ServiceName, 1, "echo", Encoding.UTF8.GetBytes("native-hosted")), null, token);
            NativeRuntimePair.Check(DecodeBytes(echo?["1"]).SequenceEqual(Encoding.UTF8.GetBytes("native-hosted")), "Hosted unary call preserves version-one body bytes.");
            var bytes = new byte[] { 0, 255, 128, 13, 10 };
            var (pipe, output) = await ServicesScenarios.RunAsync(pair, "service", Ma2aServiceRequests.ServiceCall(ServiceName, 2, "pipe", []), bytes, token);
            NativeRuntimePair.Check(output.SequenceEqual(bytes) && Encoding.UTF8.GetString(DecodeBytes(pipe?["1"])) == bytes.Length.ToString(), "Hosted bytes interaction preserves binary data and final result.");
            await OpaqueControlAsync(pair, bytes, token);
            var (_, count) = await ServicesScenarios.RunAsync(pair, "service", Ma2aServiceRequests.ServiceCall(ServiceName, 2, "count", Encoding.UTF8.GetBytes("3")), null, token);
            NativeRuntimePair.Check(Encoding.UTF8.GetString(count) == "1\n2\n3\n", "Hosted stream delivers every ordered event.");
            await using var held = await Ma2aServiceSession.OpenAsync(pair.Caller, pair.TargetId, "service", RequestId(),
                Ma2aServiceRequests.ServiceCall(ServiceName, 2, "hold", []), token);
            NativeRuntimePair.Check((await held.ReadAsync(token)).Kind == Ma2aFrameKind.Accept, "Hosted resource is accepted.");
            await held.CancelAsync(token);
            try { await ReadTerminalAsync(held, token); throw new InvalidOperationException("Hosted resource ignored cancellation."); }
            catch (Ma2aServiceException error) when (error.Status == 6 && !error.Rejected) { }
            await NativeRuntimePair.PassedAsync("native Hosted Service discovery/unary/bytes/stream/resource cancellation using real SDK example");
        }, cancellationToken);

    internal static async Task WithHostedServiceAsync(NativeRuntimePair pair,
        Func<string, CancellationToken, Task> scenario, CancellationToken cancellationToken)
    {
        var binary = ExampleBinary(pair, "ma2a-example-service");
        var manifestStart = new ProcessStartInfo(binary) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        manifestStart.ArgumentList.Add("manifest");
        using var manifestProcess = Process.Start(manifestStart) ?? throw new InvalidOperationException("SDK example manifest command did not start.");
        var manifestOutput = manifestProcess.StandardOutput.ReadToEndAsync(cancellationToken);
        var manifestError = manifestProcess.StandardError.ReadToEndAsync(cancellationToken);
        await manifestProcess.WaitForExitAsync(cancellationToken);
        await manifestError;
        NativeRuntimePair.Check(manifestProcess.ExitCode == 0, "SDK example prints a valid manifest.");
        var register = NativeRuntimePair.Mutation("service_register");
        register["service"] = ServiceName;
        register["manifest"] = JsonNode.Parse(await manifestOutput);
        await pair.Target.CallAsync(register, cancellationToken);
        var start = NativeRuntimePair.Mutation("service_start");
        start["service"] = ServiceName;
        await pair.Target.CallAsync(start, cancellationToken);
        await pair.ExposeAsync("service", new JsonObject { ["service"] = ServiceName }, cancellationToken);
        var hostStart = new ProcessStartInfo(binary) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "serve", pair.TargetStateDirectory, ServiceName }) hostStart.ArgumentList.Add(argument);
        using var host = Process.Start(hostStart) ?? throw new InvalidOperationException("SDK example service did not start.");
        var stdout = host.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = host.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await WaitReadyAsync(pair, host, cancellationToken);
            await scenario(ServiceName, cancellationToken);
        }
        finally
        {
            var stop = NativeRuntimePair.Mutation("service_stop");
            stop["service"] = ServiceName;
            try { await pair.Target.CallAsync(stop, CancellationToken.None); }
            finally
            {
                if (!host.HasExited) host.Kill(entireProcessTree: true);
                await host.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
            }
        }
    }

    private static async Task OpaqueControlAsync(NativeRuntimePair pair, byte[] content, CancellationToken cancellationToken)
    {
        await using var session = await Ma2aServiceSession.OpenAsync(pair.Caller, pair.TargetId, "service", RequestId(),
            Ma2aServiceRequests.ServiceCall(ServiceName, 2, "pipe", []), cancellationToken);
        NativeRuntimePair.Check((await session.ReadAsync(cancellationToken)).Kind == Ma2aFrameKind.Accept, "Hosted pipe accepts the control test.");
        await session.ControlBytesAsync(new byte[] { 255, 0, 192, 255 }, cancellationToken);
        await session.SendDataAsync(0, content, cancellationToken);
        await session.EndAsync(cancellationToken: cancellationToken);
        using var output = new MemoryStream();
        while (true)
        {
            var frame = await session.ReadAsync(cancellationToken);
            if (frame.Kind == Ma2aFrameKind.Data) await output.WriteAsync(frame.Data, cancellationToken);
            if (frame.Kind != Ma2aFrameKind.Outcome) continue;
            NativeRuntimePair.Check(output.ToArray().SequenceEqual(content), "Non-CBOR outbound Control does not invalidate the hosted bytes interaction.");
            NativeRuntimePair.Check(Encoding.UTF8.GetString(DecodeBytes(frame.DecodeCbor()?["1"])) == content.Length.ToString(), "Hosted pipe returns a successful outcome after opaque Control.");
            return;
        }
    }

    private static byte[] DecodeBytes(JsonNode? node)
        => Convert.FromHexString(node?["$bytes"]?.GetValue<string>() ?? throw new InvalidDataException("Expected opaque hosted result bytes."));

    private static async Task WaitReadyAsync(NativeRuntimePair pair, Process host, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            NativeRuntimePair.Check(!host.HasExited, "Hosted SDK process exited before becoming ready.");
            var list = NativeRuntimePair.Command("service_hosted_list");
            list["after"] = null;
            var services = NativeRuntimePair.Payload(await pair.Target.CallAsync(list, cancellationToken)).GetProperty("services");
            if (services.EnumerateArray().Any(service => service.GetProperty("service").GetString() == ServiceName && service.GetProperty("state").GetString() == "ready")) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        throw new TimeoutException("Hosted SDK example did not report ready within its admission budget.");
    }
}
