using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Dsh.Gui.ViewModels;
using Dsh.Ma2a.Services;
using static Dsh.Ma2a.Integration.GuiScenarios;

namespace Dsh.Ma2a.Integration;

/** Exercises production GUI consumers against the actual MA2A SDK example Agent and service. */
internal static class GuiRemoteServicesScenarios
{
    private const int DuplexPayloadBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan ProcessStartupBudget = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair,
        string agentName, string serviceName, CancellationToken cancellationToken)
    {
        remote.TargetEndpoint = pair.TargetId;
        await AgentsAsync(remote, pair, agentName, cancellationToken);
        await ProcessAsync(remote, pair, cancellationToken);
        await HostedAsync(remote, pair, serviceName, cancellationToken);
    }

    private static async Task AgentsAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, string agent, CancellationToken cancellationToken)
    {
        SelectRemote(remote, "agent", "discover");
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(Labels(remote.Results).Any(label => label.Contains(agent, StringComparison.Ordinal)), "GUI discovery must show the actual exposed SDK Agent.");
        var content = new string('界', (Ma2aServiceRequests.MaximumAgentMessageBytes - "echo ".Length) / Encoding.UTF8.GetByteCount("界"));
        var task = await SubmitAsync(remote, agent, $"echo {content}", cancellationToken);
        await WatchAsync(remote, agent, task, cancellationToken);
        SelectAgentTask(remote, "get", agent, task);
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(Labels(remote.Results).Any(label => label.EndsWith(content, StringComparison.Ordinal)), "GUI Get must retain the SDK Agent's Unicode output beyond the old 4096-byte input boundary.");
        var conversation = await SubmitAsync(remote, agent, "ask question", cancellationToken);
        SelectAgentTask(remote, "send", agent, conversation);
        Text(remote, "message", "answer");
        await RunRemoteAsync(remote, cancellationToken);
        await WatchAsync(remote, agent, conversation, cancellationToken);
        SelectAgentTask(remote, "get", agent, conversation);
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(Labels(remote.Results).Any(label => label.EndsWith("question answer", StringComparison.Ordinal)), "GUI continuation must reach the same SDK Agent task.");
        await CancelAgentAsync(remote, agent, cancellationToken);
        await ArtifactAsync(remote, pair, agent, cancellationToken);
        await NativeRuntimePair.PassedAsync("GUI Agent discovery/Unicode submit/send/watch/get/cancel/artifact consumes the actual SDK Agent and verifies artifact bytes.");
    }

    private static async Task<string> SubmitAsync(Ma2aRemoteViewModel remote, string agent, string message, CancellationToken cancellationToken)
    {
        SelectRemote(remote, "agent", "submit");
        Text(remote, "agent", agent);
        Text(remote, "message", message);
        await RunRemoteAsync(remote, cancellationToken);
        return Identifier(remote);
    }

    private static void SelectAgentTask(Ma2aRemoteViewModel remote, string operation, string agent, string task)
    {
        SelectRemote(remote, "agent", operation);
        Text(remote, "agent", agent);
        Text(remote, "task_id", task);
    }

    private static async Task WatchAsync(Ma2aRemoteViewModel remote, string agent, string task, CancellationToken cancellationToken)
    {
        SelectAgentTask(remote, "watch", agent, task);
        remote.Fields.Single(field => field.Name == "after").Number = 0;
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(remote.Results.Any(node => node.Label == "Agent event") && remote.Results.Any(node => node.Label == "Outcome"), "GUI Watch must decode actual canonical CBOR events and receive the terminal task summary.");
    }

    private static async Task CancelAgentAsync(Ma2aRemoteViewModel remote, string agent, CancellationToken cancellationToken)
    {
        var sleeping = await SubmitAsync(remote, agent, "sleep 60000", cancellationToken);
        SelectAgentTask(remote, "cancel", agent, sleeping);
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(!remote.HasPending && Labels(remote.Results).Contains("[5]: 5"), "GUI cancellation must receive the actual cancelled task state.");
        await WatchAsync(remote, agent, sleeping, cancellationToken);
    }

    private static async Task ArtifactAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, string agent, CancellationToken cancellationToken)
    {
        var source = Path.Combine(pair.Root, "gui-agent-source");
        Directory.CreateDirectory(source);
        var content = Encoding.UTF8.GetBytes("GUI SDK Agent artifact: 文件\n");
        await File.WriteAllBytesAsync(Path.Combine(source, "input.txt"), content, cancellationToken);
        var expose = NativeRuntimePair.Mutation("service_expose");
        expose["space_id"] = pair.SpaceId;
        expose["kind"] = "files_root";
        foreach (var field in new[] { "root", "path", "access", "host", "port_start", "port_end", "agent", "service" }) expose[field] = null;
        expose["root"] = "gui-artifact";
        expose["path"] = source;
        expose["access"] = "read_only";
        await pair.Caller.CallAsync(expose, cancellationToken);
        var grant = NativeRuntimePair.Mutation("space_grant");
        grant["space_id"] = pair.SpaceId;
        grant["peer_endpoint_id"] = pair.TargetId;
        foreach (var capability in new[] { "files_read", "files_write", "exec", "shell", "proxy", "process", "agent", "service" }) grant[capability] = true;
        await pair.Target.CallAsync(grant, cancellationToken);
        await pair.SyncAsync(cancellationToken);
        await DownloadArtifactAsync(remote, pair, agent, content, cancellationToken);
    }

    private static async Task DownloadArtifactAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, string agent, byte[] content, CancellationToken cancellationToken)
    {
        var task = await SubmitAsync(remote, agent, $"fetch {pair.CallerId} gui-artifact /input.txt result.txt", cancellationToken);
        await WatchAsync(remote, agent, task, cancellationToken);
        SelectAgentTask(remote, "artifact", agent, task);
        var path = Path.Combine(pair.Root, "gui-agent-artifact.bin");
        Text(remote, "name", "result.txt");
        Text(remote, "local_path", path);
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(remote.Results.Any(node => node.Label == "verified transfer") && (await File.ReadAllBytesAsync(path, cancellationToken)).SequenceEqual(content), "GUI artifact download must use the verified transfer driver and preserve the published SDK bytes.");
    }

    private static string Identifier(Ma2aRemoteViewModel remote)
    {
        NativeRuntimePair.Check(!remote.HasPending && remote.Results.Any(node => node.Label == "Outcome"), "GUI mutation must complete authoritatively before its identifier is used.");
        const string prefix = "$bytes: ";
        var id = Labels(remote.Results).First(label => label.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
        NativeRuntimePair.Check(id.Length == 32, "GUI result must contain the native 16-byte task or process identifier.");
        return id;
    }

    private static async Task ProcessAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        SelectRemote(remote, "process", "process start");
        Text(remote, "program", OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/cat");
        var args = remote.Fields.Single(field => field.Name == "args");
        if (OperatingSystem.IsWindows()) { AddArgument(args, "/c"); AddArgument(args, "findstr ."); }
        remote.Fields.Single(field => field.Name == "stdin").Checked = true;
        await RunRemoteAsync(remote, cancellationToken);
        var id = Identifier(remote);
        try
        {
            await WaitForProcessAsync(remote, id, 2, cancellationToken);
            var input = string.Concat(Enumerable.Repeat($"{new string('p', 63)}\n", 2048));
            SelectRemote(remote, "process", "process stdin");
            Text(remote, "process_id", id);
            Text(remote, "input", input);
            remote.Fields.Single(field => field.Name == "close").Checked = true;
            await RunRemoteAsync(remote, cancellationToken);
            NativeRuntimePair.Check(!remote.HasPending && remote.Results.Any(node => node.Label == "Outcome"), "GUI Process stdin must deliver multi-frame text beyond the old 4096-byte bound.");
            await WaitForProcessAsync(remote, id, 3, cancellationToken);
            await AssertProcessLogsAsync(remote, pair, id, input, cancellationToken);
        }
        finally
        {
            remote.RawOutputPath = "";
            var stop = NativeRuntimePair.Mutation("process_terminate");
            stop["process_id"] = id;
            stop["grace_ms"] = 0;
            await pair.Target.CallAsync(stop, CancellationToken.None);
        }
    }

    private static async Task WaitForProcessAsync(Ma2aRemoteViewModel remote, string id, byte state, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            SelectRemote(remote, "process", "process inspect");
            Text(remote, "process_id", id);
            await RunRemoteAsync(remote, cancellationToken);
            var summary = remote.Results.Single(node => node.Label == "Outcome").Children.Single(node => node.Label == "1")
                .Children.Single(node => node.Label == "[1]").Children.Single(node => node.Label == "[0]");
            if (summary.Children.Any(node => node.Label == $"[2]: {state}")) return;
            NativeRuntimePair.Check(clock.Elapsed < ProcessStartupBudget, "The GUI-inspected durable process must reach its expected state within the existing lifecycle budget.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static async Task AssertProcessLogsAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, string id, string input, CancellationToken cancellationToken)
    {
        SelectRemote(remote, "process", "process logs");
        Text(remote, "process_id", id);
        remote.Fields.Single(field => field.Name == "cursor").Number = 0;
        remote.RawOutputPath = Path.Combine(pair.Root, "gui-process-logs.bin");
        await RunRemoteAsync(remote, cancellationToken);
        var actual = await File.ReadAllTextAsync(remote.RawOutputPath, cancellationToken);
        NativeRuntimePair.Check(actual.Replace("\r\n", "\n", StringComparison.Ordinal) == input, "GUI logs must contain every byte of the closed stdin input, with only platform newline normalization.");
        remote.RawOutputPath = "";
        await NativeRuntimePair.PassedAsync("GUI durable Process start/inspect/multi-frame stdin/logs preserve the complete payload beyond the former text limit.");
    }

    private static async Task HostedAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, string service, CancellationToken cancellationToken)
    {
        SelectRemote(remote, "service", "describe");
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(Labels(remote.Results).Any(label => label.Contains(service, StringComparison.Ordinal)), "GUI describe must render the actual ready hosted service and its versions/operations.");
        var body = new byte[Ma2aServiceRequests.MaximumHostedCallBodyBytes];
        Random.Shared.NextBytes(body);
        SelectCall(remote, service, 1, "echo", body);
        var earlyInput = Path.Combine(pair.Root, "gui-hosted-early-input.bin");
        await File.WriteAllBytesAsync(earlyInput, new byte[DuplexPayloadBytes], cancellationToken);
        remote.Fields.Single(field => field.Name == "input_file").IsNull = false;
        Text(remote, "input_file", earlyInput);
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(!remote.HasPending && Labels(remote.Results).Contains($"$bytes: {Convert.ToHexStringLower(body)}"), "GUI must admit the maximum inline body and preserve an early successful unary result even while input sending is interrupted.");
        await HostedPipeAsync(remote, pair, service, cancellationToken);
        await HostedInteractiveAsync(remote, pair, service, cancellationToken);
        await HostedCountAsync(remote, pair, service, cancellationToken);
        await HostedCancelAsync(remote, service, cancellationToken);
    }

    private static void SelectCall(Ma2aRemoteViewModel remote, string service, ushort version, string operation, byte[] body)
    {
        SelectRemote(remote, "service", "call");
        Text(remote, "service", service);
        remote.Fields.Single(field => field.Name == "version").Number = version;
        Text(remote, "operation", operation);
        Text(remote, "body_hex", Convert.ToHexStringLower(body));
        Text(remote, "input_hex", "");
        Text(remote, "input_file", "");
        remote.Fields.Single(field => field.Name == "input_file").IsNull = true;
        remote.Fields.Single(field => field.Name == "keep_input_open").Checked = false;
    }

    private static async Task HostedPipeAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, string service, CancellationToken cancellationToken)
    {
        var bytes = new byte[DuplexPayloadBytes];
        Random.Shared.NextBytes(bytes);
        SelectCall(remote, service, 2, "pipe", []);
        var inputFile = Path.Combine(pair.Root, "gui-hosted-duplex-input.bin");
        await File.WriteAllBytesAsync(inputFile, bytes, cancellationToken);
        remote.Fields.Single(field => field.Name == "input_file").IsNull = false;
        Text(remote, "input_file", inputFile);
        remote.RawOutputPath = Path.Combine(pair.Root, "gui-hosted-duplex.bin");
        try
        {
            await RunRemoteAsync(remote, cancellationToken);
            NativeRuntimePair.Check(!remote.HasPending && (await File.ReadAllBytesAsync(remote.RawOutputPath, cancellationToken)).SequenceEqual(bytes), "GUI must concurrently drain a multi-megabyte hosted echo while sending input, preserving every frame without a duplex backpressure deadlock.");
            NativeRuntimePair.Check(Labels(remote.Results).Contains($"$bytes: {Convert.ToHexStringLower(Encoding.UTF8.GetBytes(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)))}"), "GUI pipe must render the authoritative final byte count.");
        }
        finally { remote.RawOutputPath = ""; }
    }

    private static async Task HostedInteractiveAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, string service, CancellationToken cancellationToken)
    {
        byte[] initial = [0, 255, 128];
        var additional = new byte[Ma2aServiceSession.MaximumDataLength + 1];
        Random.Shared.NextBytes(additional);
        SelectCall(remote, service, 2, "pipe", []);
        Text(remote, "input_hex", Convert.ToHexStringLower(initial));
        remote.Fields.Single(field => field.Name == "keep_input_open").Checked = true;
        remote.RawOutputPath = Path.Combine(pair.Root, "gui-hosted-interactive.bin");
        remote.Confirmed = true;
        var running = remote.RunCommand.ExecuteAsync(null);
        try
        {
            await UntilAsync(() => remote.Results.Any(node => node.Label.StartsWith("channel 1:", StringComparison.Ordinal)), "GUI hosted pipe must admit and consume its initial bytes before interactive writes.", cancellationToken);
            remote.ControlHex = "ff00c0ff";
            await remote.SendControlCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
            remote.InputHex = Convert.ToHexStringLower(additional);
            await remote.SendBytesCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
            await remote.EndInputCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
            await running.WaitAsync(cancellationToken);
            NativeRuntimePair.Check(!remote.HasPending && (await File.ReadAllBytesAsync(remote.RawOutputPath, cancellationToken)).SequenceEqual(initial.Concat(additional)), "GUI input/control/end commands must preserve exact binary input and reach a successful terminal result.");
            await NativeRuntimePair.PassedAsync("GUI hosted describe/call/max-body/large-duplex/input/raw-control/end uses the real SDK pipe; the SDK ignores Control, so this proves delivery compatibility rather than a Control echo.");
        }
        finally { await StopRunningAsync(remote, running); remote.RawOutputPath = ""; }
    }

    private static async Task HostedCountAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, string service, CancellationToken cancellationToken)
    {
        SelectCall(remote, service, 2, "count", Encoding.UTF8.GetBytes("3"));
        remote.Fields.Single(field => field.Name == "keep_input_open").Checked = true;
        remote.RawOutputPath = Path.Combine(pair.Root, "gui-hosted-stream.bin");
        try
        {
            await RunRemoteAsync(remote, cancellationToken);
            NativeRuntimePair.Check(await File.ReadAllTextAsync(remote.RawOutputPath, cancellationToken) == "1\n2\n3\n", "GUI hosted stream must preserve every ordered SDK output event.");
        }
        finally { remote.RawOutputPath = ""; }
    }

    private static async Task HostedCancelAsync(Ma2aRemoteViewModel remote, string service, CancellationToken cancellationToken)
    {
        SelectCall(remote, service, 2, "hold", []);
        remote.Fields.Single(field => field.Name == "keep_input_open").Checked = true;
        remote.Confirmed = true;
        var running = remote.RunCommand.ExecuteAsync(null);
        try
        {
            await UntilAsync(() => remote.Results.Any(node => node.Label == "Accept"), "GUI hosted resource must be admitted before detaching.", cancellationToken);
            var request = remote.RequestId;
            await remote.DetachCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
            await running.WaitAsync(cancellationToken);
            NativeRuntimePair.Check(!remote.Busy && remote.HasPending && remote.PendingRequestId == request, "GUI detach must preserve an admitted mutation's unknown outcome and stable RequestId.");
            await NativeRuntimePair.PassedAsync("GUI hosted stream and resource detach preserve ordered bytes and the admitted request's unknown-outcome identity.");
        }
        finally { await StopRunningAsync(remote, running); }
    }

    private static async Task StopRunningAsync(Ma2aRemoteViewModel remote, Task running)
    {
        if (!running.IsCompleted) await remote.DetachCommand.ExecuteAsync(null);
        await running;
    }
}
