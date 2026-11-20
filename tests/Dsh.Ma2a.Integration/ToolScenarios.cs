using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Ma2a.Tools;
using Dsh.Runtime;

namespace Dsh.Ma2a.Integration;

internal static class ToolScenarios
{
    private const int CancellationIterations = 33;
    private static readonly TimeSpan ReadCancellationDelay = TimeSpan.FromMilliseconds(100);

    public static async Task RunAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var context = new Context();
        _ = new SystemPrompt(context, new SystemPromptConfig());
        var runtime = new ToolRuntime(context);
        _ = new LlmRuntime(context);
        _ = new SafetyPolicySource(context, () => new HarnessSettings { Safety = new SafetySettings { AutoApprove = true } });
        using var tools = new Ma2aTools();
        tools.Register(context);
        using var owner = CreateAgent(context, pair.Root);
        using var other = CreateAgent(context, pair.Root);
        await OwnerAndTerminalAsync(runtime, owner, other, pair, cancellationToken);
        await CancelledReadsAsync(runtime, owner, pair, cancellationToken);
    }

    private static AgentLoopAgent CreateAgent(Context context, string cwd)
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        var session = Session.Create(id, null, new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = cwd, CreatedAt = 0, IsSeeded = false,
        });
        session.Append(new TurnStartPayload(1));
        return new AgentLoopAgent(context, id, new AgentOptions(), session, _ => 0);
    }

    private static async Task OwnerAndTerminalAsync(ToolRuntime runtime, AgentLoopAgent owner, AgentLoopAgent other,
        NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var handle = await OpenPreparedAsync(runtime, owner, pair, "files.stat", new JsonObject { ["path"] = "data:/" }, cancellationToken);
        var wrongOwner = await ExecuteAsync(runtime, other, "ma2a_stream_read", Handle(handle), cancellationToken);
        NativeRuntimePair.Check(wrongOwner.IsError, "A second DSH session cannot read the first session's native service handle.");
        JsonElement frame;
        do { frame = Success(await ExecuteAsync(runtime, owner, "ma2a_stream_read", Handle(handle), cancellationToken)); }
        while (frame.GetProperty("kind").GetString() != "Outcome");
        var ended = await ExecuteAsync(runtime, owner, "ma2a_stream_read", Handle(handle), cancellationToken);
        NativeRuntimePair.Check(ended.IsError, "Reading the terminal outcome automatically removes the native handle.");
        Success(await ExecuteAsync(runtime, owner, "ma2a_stream_close", Handle(handle), cancellationToken));
        await NativeRuntimePair.PassedAsync("Actual DSH ToolRuntime native request preparation, cross-session handle rejection, terminal release and idempotent close.");
    }

    private static async Task CancelledReadsAsync(ToolRuntime runtime, AgentLoopAgent owner, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var arguments = new JsonObject
        {
            ["program"] = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            ["args"] = OperatingSystem.IsWindows()
                ? new JsonArray("-NoProfile", "-NonInteractive", "-Command", "[Console]::In.ReadToEnd() | Out-Null")
                : new JsonArray("-c", "cat"),
            ["stdin"] = true,
        };
        for (var index = 0; index < CancellationIterations; index++)
        {
            var handle = await OpenPreparedAsync(runtime, owner, pair, "exec.run", arguments, cancellationToken);
            var admission = Success(await ExecuteAsync(runtime, owner, "ma2a_stream_read", Handle(handle), cancellationToken));
            NativeRuntimePair.Check(admission.GetProperty("kind").GetString() == "Accept", "The remote process accepts the native Exec stream before cancellation.");
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancelled.CancelAfter(ReadCancellationDelay);
            var read = await ExecuteAsync(runtime, owner, "ma2a_stream_read", Handle(handle), cancelled.Token);
            NativeRuntimePair.Check(read.IsError && cancelled.IsCancellationRequested, "An in-flight native stream read observes caller cancellation.");
        }
        var finalHandle = await OpenPreparedAsync(runtime, owner, pair, "files.stat", new JsonObject { ["path"] = "data:/" }, cancellationToken);
        Success(await ExecuteAsync(runtime, owner, "ma2a_stream_close", Handle(finalHandle), cancellationToken));
        await NativeRuntimePair.PassedAsync("Repeated cancellation beyond the native handle quota releases sessions and admits subsequent real operations.");
    }

    private static async Task<string> OpenPreparedAsync(ToolRuntime runtime, AgentLoopAgent owner, NativeRuntimePair pair,
        string operation, JsonObject arguments, CancellationToken cancellationToken)
    {
        var prepared = Success(await ExecuteAsync(runtime, owner, "ma2a_service_prepare", new JsonObject
        { ["operation"] = operation, ["arguments"] = arguments.DeepClone() }, cancellationToken));
        var opened = Success(await ExecuteAsync(runtime, owner, "ma2a_stream_open", new JsonObject
        {
            ["state_directory"] = pair.CallerStateDirectory, ["target"] = pair.TargetId,
            ["service"] = prepared.GetProperty("service").GetString(), ["request_id"] = Guid.NewGuid().ToString("N"),
            ["body"] = JsonNode.Parse(prepared.GetProperty("body").GetRawText()),
        }, cancellationToken));
        return opened.GetProperty("handle").GetString()!;
    }

    private static JsonObject Handle(string handle) => new() { ["handle"] = handle };
    private static JsonElement Success(ToolExecutionResult result)
        => result is ToolExecutionResult.Success success ? success.Value : throw new InvalidOperationException("A native MA2A model-tool call failed.");
    private static Task<ToolExecutionResult> ExecuteAsync(ToolRuntime runtime, AgentLoopAgent agent, string name,
        JsonObject arguments, CancellationToken cancellationToken)
        => runtime.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"ma2a-{Guid.NewGuid():N}"), Name = name,
            Arguments = JsonSerializer.SerializeToElement(arguments), Agent = agent, Signal = cancellationToken,
        });
}
