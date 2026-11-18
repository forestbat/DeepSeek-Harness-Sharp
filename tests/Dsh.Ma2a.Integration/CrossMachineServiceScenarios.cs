using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Integration;

internal static class CrossMachineServiceScenarios
{
    private const int MaximumOutputBytes = 65_536;
    private const ulong CompletedTaskState = 3;
    private const ulong CancelledTaskState = 5;
    private const byte CancelledStatus = 6;
    private const ulong CancelledProcessReason = 2;

    public static async Task RunAsync(Ma2aClient client, string target, string agent, string service,
        ushort proxyPort, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentOutOfRangeException.ThrowIfZero(proxyPort);
        _ = Ma2aServiceSession.DecodeIdentifier(target, 32);
        await ShellAsync(client, target, cancellationToken);
        await AgentAsync(client, target, agent, cancellationToken);
        await HostedAsync(client, target, service, cancellationToken);
        await ProxyAsync(client, target, proxyPort, cancellationToken);
        await NativeRuntimePair.PassedAsync("Cross-machine native Shell, Agent task lifecycle, Hosted Service and Proxy scenarios.");
    }

    private static string RequestId() => Guid.NewGuid().ToString("N");

    private static async Task ShellAsync(Ma2aClient client, string target, CancellationToken cancellationToken)
    {
        const string command = """printf 'cross-machine-shell\n'; read ignored; stty size; read ignored""";
        await using var session = await Ma2aServiceSession.OpenAsync(client, target, "shell", RequestId(),
            Ma2aServiceRequests.Shell("/bin/sh", ["-c", command]), cancellationToken);
        NativeRuntimePair.Check((await session.ReadAsync(cancellationToken)).Kind == Ma2aFrameKind.Accept,
            "The remote Shell must admit a real Linux PTY program.");
        await session.ControlAsync(Ma2aServiceRequests.Resize(100, 30), cancellationToken);
        await session.SendDataAsync(0, "continue\n"u8.ToArray(), cancellationToken);
        await ReadShellSizeAsync(session, cancellationToken);
        await session.CancelAsync(cancellationToken);
        var outcome = await ReadTerminalAsync(session, cancellationToken);
        NativeRuntimePair.Check(session.IsTerminal && outcome?["1"]?[1]?.GetValue<ulong>() == CancelledProcessReason,
            "Remote Shell cancellation must reach its native cancelled terminal reason.");
        await NativeRuntimePair.PassedAsync("Cross-machine Shell returns real PTY output, reports the requested 30x100 size and reaches its cancellation outcome.");
    }

    private static async Task ReadShellSizeAsync(Ma2aServiceSession session, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        while (output.Length < MaximumOutputBytes)
        {
            var frame = await session.ReadAsync(cancellationToken);
            NativeRuntimePair.Check(frame.Kind == Ma2aFrameKind.Data && frame.Channel == 1,
                "The remote Shell must emit PTY output before its cancellation.");
            if (output.Length + frame.Data.Length > MaximumOutputBytes) throw new InvalidDataException("Remote Shell output exceeded its acceptance bound.");
            await output.WriteAsync(frame.Data, cancellationToken);
            var text = Encoding.UTF8.GetString(output.ToArray()).Replace("\r", "", StringComparison.Ordinal);
            if (text.Contains("cross-machine-shell\n", StringComparison.Ordinal) && text.Contains("30 100\n", StringComparison.Ordinal)) return;
        }
        throw new InvalidDataException("The remote Shell did not report the requested PTY dimensions within the output bound.");
    }

    private static async Task AgentAsync(Ma2aClient client, string target, string agent, CancellationToken cancellationToken)
    {
        var (discovered, _) = await CrossMachineScenarios.CallAsync(client, target, "agent", Ma2aServiceRequests.Agents(), null, cancellationToken);
        NativeRuntimePair.Check(discovered?["1"]?[1] is JsonArray cards
            && cards.Any(card => card?[0]?.GetValue<string>() == agent && card?[2]?.GetValue<bool>() == true),
            "Remote Agent discovery must contain the exact available SDK Agent.");
        // Synthetic SDK commands exercise the real provider, without substituting an LLM or Agent implementation.
        var echoed = await SubmitAsync(client, target, agent, "echo cross-machine-agent", cancellationToken);
        await WatchAsync(client, target, agent, echoed, CompletedTaskState, cancellationToken);
        await VerifyTaskResultAsync(client, target, agent, echoed, "cross-machine-agent", cancellationToken);
        var conversation = await SubmitAsync(client, target, agent, "ask question", cancellationToken);
        await CrossMachineScenarios.CallAsync(client, target, "agent", Ma2aServiceRequests.AgentSend(agent, conversation, "answer"), null, cancellationToken);
        await WatchAsync(client, target, agent, conversation, CompletedTaskState, cancellationToken);
        await VerifyTaskResultAsync(client, target, agent, conversation, "question answer", cancellationToken);
        var sleeping = await SubmitAsync(client, target, agent, "sleep 60000", cancellationToken);
        var (cancelled, _) = await CrossMachineScenarios.CallAsync(client, target, "agent", Ma2aServiceRequests.AgentCancel(agent, sleeping), null, cancellationToken);
        NativeRuntimePair.Check(cancelled?["1"]?[1]?[5]?.GetValue<ulong>() == CancelledTaskState,
            "Remote Agent Cancel must return the cancelled task state.");
        await WatchAsync(client, target, agent, sleeping, CancelledTaskState, cancellationToken);
        await NativeRuntimePair.PassedAsync("Cross-machine SDK Agent discovery, submit, continuation, retained events, get and task cancellation reach their actual terminal states.");
    }

    private static async Task<string> SubmitAsync(Ma2aClient client, string target, string agent, string text, CancellationToken cancellationToken)
    {
        var (response, _) = await CrossMachineScenarios.CallAsync(client, target, "agent", Ma2aServiceRequests.AgentSubmit(agent, text), null, cancellationToken);
        return response?["1"]?[1]?[0]?["$bytes"]?.GetValue<string>()
            ?? throw new InvalidDataException("The remote Agent submit omitted its task identifier.");
    }

    private static async Task VerifyTaskResultAsync(Ma2aClient client, string target, string agent, string task,
        string expected, CancellationToken cancellationToken)
    {
        var (response, _) = await CrossMachineScenarios.CallAsync(client, target, "agent", Ma2aServiceRequests.AgentGet(agent, task), null, cancellationToken);
        NativeRuntimePair.Check(response?["1"]?[1]?[0]?[5]?.GetValue<ulong>() == CompletedTaskState
            && response?["1"]?[1]?[2]?[1]?[0]?[1]?.GetValue<string>() == expected,
            "Remote Agent Get must return the completed task and exact SDK result.");
    }

    private static async Task WatchAsync(Ma2aClient client, string target, string agent, string task,
        ulong expectedState, CancellationToken cancellationToken)
    {
        await using var session = await Ma2aServiceSession.OpenAsync(client, target, "agent", RequestId(),
            Ma2aServiceRequests.AgentWatch(agent, task), cancellationToken);
        var events = 0;
        while (true)
        {
            var frame = await session.ReadAsync(cancellationToken);
            if (frame.Kind == Ma2aFrameKind.Data)
            {
                NativeRuntimePair.Check(frame.Channel == 1 && Ma2aCbor.Decode(frame.Data) is JsonArray,
                    "Remote Agent Watch must preserve typed CBOR events on its output channel.");
                events++;
            }
            if (frame.Kind != Ma2aFrameKind.Outcome) continue;
            NativeRuntimePair.Check(session.IsTerminal && events > 0
                && frame.DecodeCbor()?["1"]?[1]?[5]?.GetValue<ulong>() == expectedState,
                "Remote Agent Watch must finish with retained events and the expected terminal task state.");
            return;
        }
    }

    private static async Task HostedAsync(Ma2aClient client, string target, string service, CancellationToken cancellationToken)
    {
        var (described, _) = await CrossMachineScenarios.CallAsync(client, target, "service", Ma2aServiceRequests.Describe(), null, cancellationToken);
        NativeRuntimePair.Check(described?["1"]?[0] is JsonArray descriptors
            && descriptors.Any(item => item?[0]?.GetValue<ulong>() == 1 && item?[1]?.GetValue<string>() == service && item?[4]?.GetValue<bool>() == true),
            "Remote discovery must identify the exact available hosted service.");
        // Synthetic opaque bytes exercise the actual SDK service body and its maximum native admission bound.
        var body = new byte[Ma2aServiceRequests.MaximumHostedCallBodyBytes];
        Random.Shared.NextBytes(body);
        var (echoed, _) = await CrossMachineScenarios.CallAsync(client, target, "service", Ma2aServiceRequests.ServiceCall(service, 1, "echo", body), null, cancellationToken);
        NativeRuntimePair.Check(DecodeBytes(echoed?["1"]).SequenceEqual(body), "Remote Hosted echo must preserve every opaque admission byte.");
        await HostedPipeAsync(client, target, service, cancellationToken);
        var (counted, output) = await CrossMachineScenarios.CallAsync(client, target, "service",
            Ma2aServiceRequests.ServiceCall(service, 2, "count", "3"u8), null, cancellationToken);
        NativeRuntimePair.Check(output.SequenceEqual("1\n2\n3\n"u8.ToArray()) && DecodeBytes(counted?["1"]).SequenceEqual("3"u8.ToArray()),
            "Remote Hosted count must preserve ordered stream bytes and the exact successful terminal result.");
        await HostedCancellationAsync(client, target, service, cancellationToken);
        await NativeRuntimePair.PassedAsync("Cross-machine Hosted describe, maximum-body unary echo, opaque control, pipe bytes, ordered stream and resource cancellation complete through the real SDK service.");
    }

    private static async Task HostedPipeAsync(Ma2aClient client, string target, string service, CancellationToken cancellationToken)
    {
        byte[] content = [0, 255, 128, 13, 10];
        await using var session = await Ma2aServiceSession.OpenAsync(client, target, "service", RequestId(),
            Ma2aServiceRequests.ServiceCall(service, 2, "pipe", []), cancellationToken);
        NativeRuntimePair.Check((await session.ReadAsync(cancellationToken)).Kind == Ma2aFrameKind.Accept, "Remote Hosted pipe must accept the byte stream.");
        await session.ControlBytesAsync(new byte[] { 255, 0, 192, 255 }, cancellationToken);
        await session.SendDataAsync(0, content, cancellationToken);
        await session.EndAsync(cancellationToken: cancellationToken);
        using var output = new MemoryStream();
        while (true)
        {
            var frame = await session.ReadAsync(cancellationToken);
            if (frame.Kind == Ma2aFrameKind.Data)
            {
                NativeRuntimePair.Check(frame.Channel == 1 && output.Length + frame.Data.Length <= content.Length,
                    "Remote Hosted pipe must only return the expected bounded output bytes.");
                await output.WriteAsync(frame.Data, cancellationToken);
            }
            if (frame.Kind != Ma2aFrameKind.Outcome) continue;
            NativeRuntimePair.Check(session.IsTerminal && output.ToArray().SequenceEqual(content)
                && Encoding.UTF8.GetString(DecodeBytes(frame.DecodeCbor()?["1"])) == content.Length.ToString(CultureInfo.InvariantCulture),
                "Remote Hosted pipe must preserve binary bytes and its verified terminal count after opaque Control.");
            return;
        }
    }

    private static async Task HostedCancellationAsync(Ma2aClient client, string target, string service, CancellationToken cancellationToken)
    {
        await using var session = await Ma2aServiceSession.OpenAsync(client, target, "service", RequestId(),
            Ma2aServiceRequests.ServiceCall(service, 2, "hold", []), cancellationToken);
        NativeRuntimePair.Check((await session.ReadAsync(cancellationToken)).Kind == Ma2aFrameKind.Accept, "Remote Hosted hold must be admitted before cancellation.");
        await session.CancelAsync(cancellationToken);
        try
        {
            await ReadTerminalAsync(session, cancellationToken);
            throw new InvalidOperationException("Remote Hosted hold returned success instead of a cancelled outcome.");
        }
        catch (Ma2aServiceException error) when (error.Status == CancelledStatus && !error.Rejected && session.IsTerminal) { }
    }

    private static async Task ProxyAsync(Ma2aClient client, string target, ushort port, CancellationToken cancellationToken)
    {
        // Synthetic binary input reaches a real TCP echo listener started independently on the target machine.
        byte[] content = [0, 1, 255, 128, 10, 13];
        await using var session = await Ma2aServiceSession.OpenAsync(client, target, "proxy", RequestId(),
            Ma2aServiceRequests.Proxy("127.0.0.1", port), cancellationToken);
        NativeRuntimePair.Check((await session.ReadAsync(cancellationToken)).Kind == Ma2aFrameKind.Accept, "Remote Proxy must admit the exact TCP destination.");
        await session.SendDataAsync(0, content, cancellationToken);
        await session.EndAsync(cancellationToken: cancellationToken);
        using var output = new MemoryStream();
        var ended = false;
        while (true)
        {
            var frame = await session.ReadAsync(cancellationToken);
            if (frame.Kind == Ma2aFrameKind.Data)
            {
                NativeRuntimePair.Check(frame.Channel == 0 && output.Length + frame.Data.Length <= content.Length,
                    "Remote Proxy must preserve its channel and exact output bound.");
                await output.WriteAsync(frame.Data, cancellationToken);
            }
            if (frame.Kind == Ma2aFrameKind.End) { NativeRuntimePair.Check(frame.Channel == 0, "Remote Proxy half-close must use channel zero."); ended = true; }
            if (frame.Kind != Ma2aFrameKind.Outcome) continue;
            var result = frame.DecodeCbor()?["1"];
            NativeRuntimePair.Check(session.IsTerminal && ended && output.ToArray().SequenceEqual(content)
                && result?[0]?.GetValue<ulong>() == 0 && result?[1]?.GetValue<ulong>() == (ulong)content.Length
                && result?[2]?.GetValue<ulong>() == (ulong)content.Length,
                "Remote Proxy must reach a completed half-close outcome with exact binary bytes and both native counters.");
            break;
        }
        await NativeRuntimePair.PassedAsync("Cross-machine Proxy forwards exact binary bytes through a real TCP peer, observes half-close and verifies both terminal counters.");
    }

    private static byte[] DecodeBytes(JsonNode? value)
        => Convert.FromHexString(value?["$bytes"]?.GetValue<string>() ?? throw new InvalidDataException("Missing hosted result byte string."));

    private static async Task<JsonNode?> ReadTerminalAsync(Ma2aServiceSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await session.ReadAsync(cancellationToken);
            if (frame.Kind == Ma2aFrameKind.Outcome) return frame.DecodeCbor();
        }
    }
}
