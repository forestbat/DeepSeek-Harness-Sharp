using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.Terminal;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Ma2a.Services;
using Dsh.Runtime;

namespace Dsh.Ma2a.Integration;

internal static class GuiScenarios
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(10);
    private const string TerminalMarker = "ma2a-gui-native-consumer";
    private const string StaleMarker = "acceptance-stale-projection";
    private const int InvitationLifetimeMilliseconds = 300_000;
    private const string OutboundProbeUrl = "http://example.invalid/ma2a-gui-outbound?probe=1";
    private const string OutboundProbeBody = "MA2A native TCP tunnel reached the DSH outbound HTTP consumer.";
    private const int MaximumProbeHeaders = 64;
    private const int MaximumProbeHeaderBytes = 16_384;

    public static async Task RunAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(Ma2aGuiAppBuilder), AvaloniaTestIsolationLevel.PerTest);
        await session.Dispatch(async () =>
        {
            await RunConsoleAsync(pair, cancellationToken);
            return true;
        }, cancellationToken).ContinueWith(static task => task.GetAwaiter().GetResult(),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).ConfigureAwait(false);
        await pair.Caller.CallAsync(NativeRuntimePair.Command("status"), cancellationToken);
        await pair.Target.CallAsync(NativeRuntimePair.Command("status"), cancellationToken);
        await NativeRuntimePair.PassedAsync("Disposing real GUI views leaves both task-owned MA2A Runtimes running.");
    }

    private static async Task RunConsoleAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        using var caller = new Ma2aViewModel { StateDirectory = pair.CallerStateDirectory, Executable = pair.BinaryPath };
        using var target = new Ma2aViewModel { StateDirectory = pair.TargetStateDirectory, Executable = pair.BinaryPath };
        using var canceled = cancellationToken.Register(() => { caller.Dispose(); target.Dispose(); });
        var view = new Ma2aView { DataContext = caller };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        try
        {
            window.Show();
            await caller.ConnectCommand.ExecuteAsync(null);
            await target.ConnectCommand.ExecuteAsync(null);
            NativeRuntimePair.Check(caller.Connected && caller.Endpoint == pair.CallerId, "The GUI must attach to the real caller identity.");
            NativeRuntimePair.Check(target.Connected && target.Endpoint == pair.TargetId, "The operator GUI must attach to the real target identity.");
            NativeRuntimePair.Check(Labels(caller.Snapshot).Contains($"space id: {pair.SpaceId}"), "The caller GUI must show its signed Space membership.");
            await InspectManagementAsync(target, pair, cancellationToken);
            await NativeRuntimePair.PassedAsync("Real MA2A GUI attaches, renders Endpoint/Space snapshots, members, grants and local exposures.");
            await SameRevisionTelemetryAsync(caller, pair, cancellationToken);
            await MutateManagementAsync(target, pair, cancellationToken);
            await FilesAsync(caller.Remote, pair, cancellationToken);
            await TransfersAsync(caller.Remote, pair, cancellationToken);
            await ExecAsync(view, window, caller.Remote, pair, cancellationToken);
            await ProxyOutboundAsync(pair, cancellationToken);
            await RestartAsync(caller, pair, cancellationToken);
            await StopAndStartAsync(caller, pair, cancellationToken);
            await GuiManagementScenarios.RunAsync(caller, target, pair, cancellationToken);
            var agent = await GuiManagementScenarios.StartAgentAsync(target, pair, cancellationToken);
            try
            {
                await AgentServiceScenarios.WithHostedServiceAsync(pair,
                    (service, token) => GuiRemoteServicesScenarios.RunAsync(caller.Remote, pair, agent, service, token), cancellationToken);
            }
            finally { await GuiManagementScenarios.StopAgentAsync(target, pair, agent, CancellationToken.None); }
        }
        finally { window.Close(); }
    }

    private static async Task InspectManagementAsync(Ma2aViewModel model, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        SelectManagement(model, "space_details_fetch");
        model.Fields.Single(field => field.Name == "space_id").Text = pair.SpaceId;
        await model.ExecuteCommand.ExecuteAsync(null);
        cancellationToken.ThrowIfCancellationRequested();
        var members = Labels(model.Result);
        NativeRuntimePair.Check(members.Contains($"endpoint id: {pair.CallerId}"), "Space details in the GUI must include the actual enrolled caller.");
        NativeRuntimePair.Check(members.Contains("files read: True"), "The GUI must render the signed Files read grant independently of connectivity.");
        SelectManagement(model, "service_exposure_list");
        await model.ExecuteCommand.ExecuteAsync(null);
        var exposures = Labels(model.Result);
        NativeRuntimePair.Check(exposures.Contains("root: data") && exposures.Contains("access: read_write"), "The target GUI must render the exposed root and its write policy.");
        NativeRuntimePair.Check(!model.HasPending, "Read-only GUI inspection must not create an indeterminate mutation.");
    }

    private static async Task FilesAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes("GUI native Files consumer: 文件\n");
        remote.TargetEndpoint = pair.TargetId;
        SelectRemote(remote, "files", "write");
        Text(remote, "path", "data:/gui-native.txt");
        Text(remote, "content_hex", Convert.ToHexStringLower(bytes));
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(!remote.HasPending && Labels(remote.Results).Contains("Outcome"), "GUI native Files write must reach a terminal response.");
        SelectRemote(remote, "files", "list");
        Text(remote, "path", "data:/");
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(Labels(remote.Results).Any(label => label.Contains("gui-native.txt", StringComparison.Ordinal)), "GUI Files listing must contain the file created through its native write action.");
        var captured = Path.Combine(pair.Root, "gui-files-read.bin");
        SelectRemote(remote, "files", "read");
        Text(remote, "path", "data:/gui-native.txt");
        remote.RawOutputPath = captured;
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(!remote.HasPending, "GUI Files read must not leave an indeterminate result.");
        NativeRuntimePair.Check((await File.ReadAllBytesAsync(captured, cancellationToken)).SequenceEqual(bytes), "The GUI's raw output sink must preserve actual Files bytes, including UTF-8 content.");
        remote.RawOutputPath = "";
        await NativeRuntimePair.PassedAsync("GUI native Files write/list/read uses the real remote root and preserves exact output bytes.");
    }

    private static async Task MutateManagementAsync(Ma2aViewModel model, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var before = NativeRuntimePair.Payload(await pair.Target.CallAsync(NativeRuntimePair.Command("space_list"), cancellationToken)).GetArrayLength();
        var name = $"gui-space-{Guid.NewGuid():N}";
        SelectManagement(model, "space_create");
        model.Fields.Single(field => field.Name == "name").Text = name;
        model.Confirmed = true;
        await model.ExecuteCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
        NativeRuntimePair.Check(!model.HasPending && Labels(model.Result).Contains("type: space_created"), "GUI Space creation must receive its actual success result.");
        var spaces = NativeRuntimePair.Payload(await pair.Target.CallAsync(NativeRuntimePair.Command("space_list"), cancellationToken));
        NativeRuntimePair.Check(spaces.GetArrayLength() == before + 1 && spaces.EnumerateArray().Count(space => space.GetProperty("name").GetString() == name) == 1,
            "GUI Space creation must add exactly one uniquely named Space to the real Runtime.");
        await GrantAsync(model, pair, cancellationToken);
        await InviteAsync(model, pair, cancellationToken);
        await NativeRuntimePair.PassedAsync("GUI mutation commands create exactly one Space, preserve all eight signed grants, and write a private invitation without displaying its contents.");
    }

    private static async Task SameRevisionTelemetryAsync(Ma2aViewModel model, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        await pair.SyncAsync(cancellationToken);
        await model.RefreshCommand.ExecuteAsync(null);
        var displayedBefore = Labels(model.Snapshot.Single(node => node.Label == "control rounds").Children);
        var before = await pair.Caller.GetSnapshotAsync(cancellationToken);
        var revision = before.GetProperty("revision").GetUInt64();
        var boot = before.GetProperty("runtime_boot_id").GetString();
        NativeRuntimePair.Check(model.Connected && model.Snapshot.Any(node => node.Label == $"revision: {revision}"),
            "The baseline GUI projection must already match the actual durable revision before the no-op synchronization.");
        var roundsBefore = NativeRuntimePair.Payload(before).GetProperty("control_rounds").GetRawText();
        await pair.SyncAsync(cancellationToken);
        var after = await pair.Caller.GetSnapshotAsync(cancellationToken);
        NativeRuntimePair.Check(after.GetProperty("revision").GetUInt64() == revision && after.GetProperty("runtime_boot_id").GetString() == boot,
            "The no-op control round must keep the actual Runtime revision and boot unchanged for this telemetry regression.");
        var roundsAfter = NativeRuntimePair.Payload(after).GetProperty("control_rounds");
        NativeRuntimePair.Check(roundsAfter.GetRawText() != roundsBefore, "The real no-op synchronization must append a diagnostic control round without a durable state change.");
        await model.RefreshCommand.ExecuteAsync(null);
        await UntilAsync(() => !Labels(model.Snapshot.Single(node => node.Label == "control rounds").Children).SequenceEqual(displayedBefore),
            "GUI refresh must replace changed telemetry even when the real Runtime revision and boot remain identical.", cancellationToken);
        var expectedRound = Labels(Ma2aResultNode.ChildrenOf(roundsAfter.EnumerateArray().Last()));
        NativeRuntimePair.Check(model.Snapshot.Single(node => node.Label == "control rounds").Children.Any(round => Labels(round.Children).SequenceEqual(expectedRound)),
            "The GUI must render the actual newly observed control-round timestamp, peer count and outcome.");
        var final = NativeRuntimePair.Payload(await pair.Caller.CallAsync(NativeRuntimePair.Command("snapshot_stamp"), cancellationToken));
        NativeRuntimePair.Check(final.GetProperty("revision").GetUInt64() == revision && final.GetProperty("runtime_boot_id").GetString() == boot,
            "The GUI telemetry regression must remain within one unchanged durable revision and Runtime boot through its final assertion.");
        await NativeRuntimePair.PassedAsync("GUI refresh consumes real control-round telemetry changes while Runtime revision and boot stay identical.");
    }

    private static async Task GrantAsync(Ma2aViewModel model, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        SelectManagement(model, "space_grant");
        model.Fields.Single(field => field.Name == "space_id").Text = pair.SpaceId;
        model.Fields.Single(field => field.Name == "peer_endpoint_id").Text = pair.CallerId;
        var capabilities = model.Fields.Where(field => field.IsBoolean).ToArray();
        NativeRuntimePair.Check(capabilities.Length == 8, "The native GUI grant form must expose every signed service capability.");
        foreach (var field in capabilities) field.Checked = true;
        model.Confirmed = true;
        await model.ExecuteCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
        var result = Labels(model.Result);
        NativeRuntimePair.Check(!model.HasPending && result.Contains("type: space_granted"), "GUI grant replacement must receive its signed success result.");
        foreach (var capability in capabilities)
            NativeRuntimePair.Check(result.Contains($"{capability.Label}: True"), "GUI grant replacement must preserve all eight enabled capabilities.");
    }

    private static async Task InviteAsync(Ma2aViewModel model, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var path = Path.Combine(pair.Root, "gui-private-invitation");
        SelectManagement(model, "space_invite");
        model.Fields.Single(field => field.Name == "space_id").Text = pair.SpaceId;
        model.Fields.Single(field => field.Name == "ttl_ms").Number = InvitationLifetimeMilliseconds;
        model.Fields.Single(field => field.Name == "output_path").Text = path;
        model.Confirmed = true;
        await model.ExecuteCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
        NativeRuntimePair.Check(!model.HasPending && Labels(model.Result).Contains("type: space_invitation_created"), "GUI invitation creation must receive its success result without the ticket contents.");
        NativeRuntimePair.Check(File.Exists(path) && new FileInfo(path).Length > 0, "GUI invitation creation must write the actual ticket to the task-owned private path.");
    }

    private static async Task TransfersAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var source = Path.Combine(pair.Root, "gui-transfer-source.bin");
        var destination = Path.Combine(pair.Root, "gui-transfer-result.bin");
        byte[] content = [.. Encoding.UTF8.GetBytes("GUI transfer integrity consumer\n"), 0, 255, 128];
        var expectedHash = global::Blake3.Hasher.Hash(content).ToString();
        await File.WriteAllBytesAsync(source, content, cancellationToken);
        SelectRemote(remote, "transfer", "upload");
        Text(remote, "path", "data:/gui-transfer.bin");
        Text(remote, "local_path", source);
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(!remote.HasPending && Labels(remote.Results).Contains("verified transfer"), "GUI upload must complete through the verified transfer driver.");
        AssertTransferPresentation(remote, content.Length, expectedHash);
        SelectRemote(remote, "transfer", "download");
        Text(remote, "path", "data:/gui-transfer.bin");
        Text(remote, "local_path", destination);
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(!remote.HasPending && Labels(remote.Results).Contains("verified transfer"), "GUI download must expose the verified transfer result.");
        AssertTransferPresentation(remote, content.Length, expectedHash);
        NativeRuntimePair.Check((await File.ReadAllBytesAsync(destination, cancellationToken)).SequenceEqual(content), "GUI transfer must preserve both text and non-text bytes.");
        await NativeRuntimePair.PassedAsync("GUI upload/download uses real MA2A transfer streams and BLAKE3 verification.");
    }

    private static void AssertTransferPresentation(Ma2aRemoteViewModel remote, int size, string hash)
    {
        var result = remote.Results.Single(node => node.Label == "verified transfer");
        var labels = Labels(result.Children);
        NativeRuntimePair.Check(labels.Contains($"Bytes: {size}") && labels.Contains($"Size: {size}"),
            "The GUI must render the exact verified byte counts using its existing result field names.");
        NativeRuntimePair.Check(labels.Contains($"Blake3: {hash}"), "The GUI must render the actual verified content hash.");
    }

    private static async Task ExecAsync(Ma2aView view, Window window, Ma2aRemoteViewModel remote,
        NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        SelectRemote(remote, "exec", "exec");
        Text(remote, "program", OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
        var args = remote.Fields.Single(field => field.Name == "args");
        AddArgument(args, OperatingSystem.IsWindows() ? "/c" : "-c");
        AddArgument(args, OperatingSystem.IsWindows() ? $"echo {TerminalMarker}" : $"printf {TerminalMarker}");
        view.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2;
        await RunRemoteAsync(remote, cancellationToken);
        await UntilAsync(() => !remote.Busy && ScreenContains(remote.Terminal, TerminalMarker), "The real GUI VT terminal must render native Exec stdout and reach its terminal outcome.", cancellationToken);
        Dispatcher.UIThread.RunJobs();
        var terminal = view.GetVisualDescendants().OfType<TerminalControl>().Single();
        NativeRuntimePair.Check(ReferenceEquals(terminal.Session, remote.Terminal), "The rendered TerminalControl must consume the native MA2A terminal session.");
        NativeRuntimePair.Check(Labels(remote.Results).Contains("terminal"), "The GUI must retain native terminal admission/outcome metadata.");
        using var image = window.CaptureRenderedFrame();
        NativeRuntimePair.Check(image is not null, "The native GUI acceptance view must produce a rendered frame.");
        image!.Save(Path.Combine(pair.Root, "gui-native-terminal.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        await NativeRuntimePair.PassedAsync("Real Exec stdout reaches the existing GUI VT terminal and a rendered MA2A view.");
    }

    private static async Task RestartAsync(Ma2aViewModel model, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var before = await pair.Caller.GetSnapshotAsync(cancellationToken);
        var boot = before.GetProperty("runtime_boot_id").GetString();
        model.Snapshot.Add(new Ma2aResultNode(StaleMarker, []));
        model.LifecycleConfirmed = true;
        await model.LifecycleCommand.ExecuteAsync("restart").WaitAsync(cancellationToken);
        await model.RefreshCommand.ExecuteAsync(null);
        await UntilAsync(() => model.Connected && !model.Snapshot.Any(node => node.Label == StaleMarker), "A Runtime boot change must invalidate and replace the GUI projection.", cancellationToken);
        var after = await pair.Caller.GetSnapshotAsync(cancellationToken);
        NativeRuntimePair.Check(after.GetProperty("runtime_boot_id").GetString() != boot, "The tested GUI refresh must span an actual Runtime restart.");
        NativeRuntimePair.Check(model.Endpoint == pair.CallerId, "GUI resynchronization must retain the persistent Endpoint identity.");
        NativeRuntimePair.Check(Labels(model.Snapshot).Contains($"space id: {pair.SpaceId}"), "GUI resynchronization must restore complete Space membership.");
        await NativeRuntimePair.PassedAsync("GUI lifecycle restart resynchronizes the real Runtime and retains the persistent Endpoint identity.");
    }

    private static async Task ProxyOutboundAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        using var probeLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<HttpProbeRequest>? receiving = null;
        try
        {
            await pair.ExposeAsync("proxy", new JsonObject { ["host"] = "127.0.0.1", ["port_start"] = port, ["port_end"] = port }, cancellationToken);
            var failures = new ConcurrentQueue<string>();
            await using var forward = new Ma2aProxyForward(pair.Caller, pair.TargetId, Ma2aServiceRequests.Proxy("127.0.0.1", (ushort)port), 0, failures.Enqueue, cancellationToken);
            using var route = new OutboundProxy();
            route.Configure($"http://127.0.0.1:{forward.LocalPort}", []);
            using var client = new HttpClient(route.CreateHandler(allowAutoRedirect: false));
            receiving = ReceiveHttpProbeAsync(listener, probeLifetime.Token);
            using var response = await client.GetAsync(OutboundProbeUrl, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var observed = await receiving;
            NativeRuntimePair.Check(response.StatusCode == HttpStatusCode.OK && body.SequenceEqual(Encoding.UTF8.GetBytes(OutboundProbeBody)), "The actual DSH outbound handler must receive the exact HTTP probe response through native MA2A Proxy.");
            NativeRuntimePair.Check(observed.RequestLine == $"GET {OutboundProbeUrl} HTTP/1.1" && observed.Headers.Any(header => header.Equals("Host: example.invalid", StringComparison.OrdinalIgnoreCase)), "The target-side HTTP probe must receive the absolute-URI proxy request and its destination Host header.");
            NativeRuntimePair.Check(failures.IsEmpty, "The task-owned native TCP forward must finish without a forwarding failure.");
            await NativeRuntimePair.PassedAsync("DSH OutboundProxy consumes the MA2A GUI TCP forward; a bounded HTTP receiver probe verifies absolute-URI routing and exact bytes, without claiming external business access.");
        }
        finally { await StopHttpProbeAsync(listener, probeLifetime, receiving); }
    }

    /** This bounded receiver is an HTTP protocol probe, not a forwarding proxy or an external business service. */
    private static async Task<HttpProbeRequest> ReceiveHttpProbeAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var peer = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException("The HTTP probe received no request line.");
        var headers = new List<string>();
        var size = requestLine.Length;
        while (true)
        {
            var header = await reader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException("The HTTP probe received incomplete headers.");
            if (header.Length == 0) break;
            headers.Add(header);
            size += header.Length;
            NativeRuntimePair.Check(headers.Count <= MaximumProbeHeaders && size <= MaximumProbeHeaderBytes, "The HTTP probe request must stay within its bounded header inventory.");
        }
        var content = Encoding.UTF8.GetBytes(OutboundProbeBody);
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        await stream.WriteAsync(content, cancellationToken);
        peer.Client.Shutdown(SocketShutdown.Send);
        return new(requestLine, headers);
    }

    private static async Task StopHttpProbeAsync(TcpListener listener, CancellationTokenSource lifetime, Task<HttpProbeRequest>? receiving)
    {
        await lifetime.CancelAsync();
        listener.Stop();
        try { if (receiving is not null) await receiving; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private sealed record HttpProbeRequest(string RequestLine, IReadOnlyList<string> Headers);

    private static async Task StopAndStartAsync(Ma2aViewModel model, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        model.LifecycleConfirmed = true;
        await model.LifecycleCommand.ExecuteAsync("stop").WaitAsync(cancellationToken);
        NativeRuntimePair.Check(!model.Connected && model.Snapshot.Count == 0, "GUI lifecycle stop must detach and invalidate the Runtime projection.");
        try
        {
            await pair.Caller.CallAsync(NativeRuntimePair.Command("status"), cancellationToken);
            throw new InvalidOperationException("GUI lifecycle stop left its task-owned Runtime available.");
        }
        catch (Exception error) when (error is SocketException || error is IOException and not Ma2aProtocolException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested) { }
        model.LifecycleConfirmed = true;
        await model.LifecycleCommand.ExecuteAsync("start").WaitAsync(cancellationToken);
        NativeRuntimePair.Check(model.Connected && model.Endpoint == pair.CallerId, "GUI lifecycle start must reconnect to the same persistent Endpoint identity.");
        await pair.Caller.CallAsync(NativeRuntimePair.Command("status"), cancellationToken);
        await NativeRuntimePair.PassedAsync("GUI lifecycle stop proves Runtime absence; explicit start restores the same Endpoint and a live native connection.");
    }

    internal static void SelectManagement(Ma2aViewModel model, string operation)
    {
        var command = Ma2aCommandCatalog.Commands.Single(command => command.Operation == operation);
        model.SelectedGroup = command.Group;
        model.SelectedCommand = command;
    }

    internal static void SelectRemote(Ma2aRemoteViewModel model, string family, string operation)
        => model.SelectedOperation = model.Operations.Single(item => item.Family == family && item.Name == operation);

    internal static void Text(Ma2aRemoteViewModel model, string name, string value)
        => model.Fields.Single(field => field.Name == name).Text = value;

    internal static void AddArgument(Ma2aFieldViewModel field, string value)
    {
        field.AddItemCommand.Execute(null);
        field.Items[^1].Text = value;
    }

    internal static async Task RunRemoteAsync(Ma2aRemoteViewModel model, CancellationToken cancellationToken)
    {
        model.Confirmed = true;
        await model.RunCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
    }

    internal static IReadOnlyList<string> Labels(IEnumerable<Ma2aResultNode> nodes)
        => nodes.SelectMany(node => new[] { node.Label }.Concat(Labels(node.Children))).ToArray();

    private static bool ScreenContains(TerminalSession? terminal, string value)
    {
        if (terminal is null) return false;
        lock (terminal.Gate) return terminal.Screen.Snapshot().Contains(value, StringComparison.Ordinal);
    }

    internal static async Task UntilAsync(Func<bool> condition, string failure, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            NativeRuntimePair.Check(elapsed.Elapsed < Arrival, failure);
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }
}

public static class Ma2aGuiAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Dsh.Gui.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
}
