using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Subagent;

namespace Dsh.Tests;

public class SubagentInterruptionTests
{
    private static readonly TimeSpan RunCompletionTimeout = TimeSpan.FromSeconds(30);

    private sealed class ControlledAdapter(
        Func<GenerateOptions, CancellationToken, IAsyncEnumerable<StreamChunk>> handler) : LlmAdapter
    {
        public List<GenerateOptions> Requests { get; } = [];

        public override LlmProviderInfo ProviderInfo { get; } = new("test-provider", "test-provider");

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
        {
            Requests.Add(options);
            return handler(options, cancellationToken);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable _spawn;
        private readonly IDisposable _headless;

        private Fixture(Func<GenerateOptions, CancellationToken, IAsyncEnumerable<StreamChunk>> handler)
        {
            Ctx = new Context();
            Sessions = new SessionStore(Ctx);
            Prompts = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Llm = new LlmRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);
            Loop = new AgentLoop(Ctx);
            Approval = ApprovalService.Register(Ctx);
            Subagents = new SubagentRuntime(Ctx);
            Adapter = new ControlledAdapter(handler);
            Llm.RegisterAdapter(["test-provider"], Adapter);
            _spawn = SubagentInProcessProviders.RegisterSpawn(Ctx);
            _headless = SubagentInProcessProviders.RegisterHeadless(Ctx);
        }

        public Context Ctx { get; }
        public SessionStore Sessions { get; }
        public SystemPrompt Prompts { get; }
        public ToolRuntime Tools { get; }
        public LlmRuntime Llm { get; }
        public AgentRegistry Agents { get; }
        public AgentLoop Loop { get; }
        public ApprovalService Approval { get; }
        public SubagentRuntime Subagents { get; }
        public ControlledAdapter Adapter { get; }

        public static Fixture Create(
            Func<GenerateOptions, CancellationToken, IAsyncEnumerable<StreamChunk>> handler)
            => new(handler);

        public async Task<AgentLoopAgent> CreateParent(string id)
        {
            var handle = await Agents.Create(new CreateAgentOptions(
                SessionId.Create(id), null, new AgentOptions("test-provider", "test-model")));
            return (AgentLoopAgent)handle.Agent;
        }

        public void Dispose()
        {
            _spawn.Dispose();
            _headless.Dispose();
        }
    }

    private static async IAsyncEnumerable<StreamChunk> StreamOf(
        IReadOnlyList<StreamChunk> prefix,
        TaskCompletionSource? started,
        bool blockAfterPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        TaskCompletionSource? release = null)
    {
        foreach (var chunk in prefix)
            yield return chunk;
        started?.TrySetResult();
        if (release is not null)
            await release.Task.WaitAsync(cancellationToken);
        else if (blockAfterPrefix)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    [Fact]
    public async Task AbortDuringLlmStream_ReportsLlmStreamPhaseWithPartialOutput()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = Fixture.Create((_, ct) => StreamOf(
            [
                new StreamChunk.BlockStart(0, "text"),
                new StreamChunk.TextDelta(0, "partial hello"),
            ],
            started, blockAfterPrefix: true, ct));
        var parent = await fixture.CreateParent("session-parent-interrupt-stream");
        using var cts = new CancellationTokenSource();

        var run = await fixture.Subagents.StartAsync("spawn", new SubagentStartRequest
        {
            Label = "stream interruption",
            Prompt = [new TextBlock("write something long")],
            Parent = parent,
            Signal = cts.Token,
        });
        await started.Task.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var result = await run.Result.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(SubagentStopReason.Aborted, result.StopReason);
        var interruption = result.Interruption;
        Assert.NotNull(interruption);
        Assert.Equal(SubagentInterruptionPhase.LlmStream, interruption.Phase);
        Assert.Equal(0, interruption.CompletedTurns);
        Assert.Contains("partial hello", interruption.PartialOutput);
        Assert.Null(interruption.PendingToolCall);
        Assert.True(interruption.Resumable);
        Assert.Equal($"{run.Id.Value}@1", interruption.CheckpointToken);
        await run.DisposeAsync();
    }

    [Fact]
    public async Task AbortDuringToolExecution_ReportsPendingToolCallAsNotResumable()
    {
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = Fixture.Create((_, ct) => StreamOf(
            [
                new StreamChunk.ToolCallDelta(0, ToolCallId.Create("call-blocker-1"), "blocker", "{}"),
                new StreamChunk.Finish(new FinishReason.ToolCalls()),
            ],
            null, blockAfterPrefix: false, ct));
        fixture.Tools.Register(new ToolDefinition
        {
            Name = "blocker",
            Description = "blocks until cancelled",
            Parameters = new JsonObject(),
            Output = new ToolOutputDefinition(new JsonObject(), (_, _) => []),
            Execute = async (_, runContext) =>
            {
                invoked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, runContext.Signal);
                return null;
            },
        });
        var parent = await fixture.CreateParent("session-parent-interrupt-tool");
        using var cts = new CancellationTokenSource();

        var run = await fixture.Subagents.StartAsync("spawn", new SubagentStartRequest
        {
            Label = "tool interruption",
            Prompt = [new TextBlock("call the blocker tool")],
            Parent = parent,
            Signal = cts.Token,
        });
        await invoked.Task.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var result = await run.Result.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(SubagentStopReason.Aborted, result.StopReason);
        var interruption = result.Interruption;
        Assert.NotNull(interruption);
        Assert.Equal(SubagentInterruptionPhase.ToolExecution, interruption.Phase);
        Assert.Equal("", interruption.PartialOutput);
        var pending = interruption.PendingToolCall;
        Assert.NotNull(pending);
        Assert.Equal("blocker", pending.ToolName);
        Assert.Equal("call-blocker-1", pending.CallId);
        Assert.Equal(SubagentPendingToolCall.UnknownOutcome, pending.Outcome);
        Assert.False(interruption.Resumable);
        await run.DisposeAsync();
    }

    [Fact]
    public async Task CompletedRun_HasNoInterruptionSnapshot()
    {
        using var fixture = Fixture.Create((_, ct) => StreamOf(
            [
                new StreamChunk.BlockStart(0, "text"),
                new StreamChunk.TextDelta(0, "done"),
                new StreamChunk.BlockEnd(0, new TextBlock("done")),
                new StreamChunk.Finish(new FinishReason.Stop()),
            ],
            null, blockAfterPrefix: false, ct));
        var parent = await fixture.CreateParent("session-parent-interrupt-none");

        var run = await fixture.Subagents.StartAsync("spawn", new SubagentStartRequest
        {
            Label = "clean run",
            Prompt = [new TextBlock("finish quickly")],
            Parent = parent,
            Signal = default,
        });

        var result = await run.Result.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(SubagentStopReason.Completed, result.StopReason);
        Assert.Null(result.Interruption);
        await run.DisposeAsync();
    }

    [Fact]
    public async Task HeadlessRun_CompletesWithoutSessionStoreEntry()
    {
        using var fixture = Fixture.Create((_, ct) => StreamOf(
            [
                new StreamChunk.BlockStart(0, "text"),
                new StreamChunk.TextDelta(0, "headless answer"),
                new StreamChunk.BlockEnd(0, new TextBlock("headless answer")),
                new StreamChunk.Finish(new FinishReason.Stop()),
            ],
            null, blockAfterPrefix: false, ct));
        var parent = await fixture.CreateParent("session-parent-headless");
        var storeSizeAfterParent = fixture.Sessions.List().Count;
        var sessionStarts = 0;
        var subscription = fixture.Ctx.On<AgentSessionStartNotification>(
            _ => sessionStarts++,
            new EventOptions { Global = true });
        var ends = new List<SubagentRunEndInfo>();
        var endSubscription = fixture.Ctx.On<SubagentEndNotification>(
            notification => ends.Add(notification.Info),
            new EventOptions { Global = true });

        var run = await fixture.Subagents.StartAsync("headless", new SubagentStartRequest
        {
            Label = "headless run",
            Prompt = [new TextBlock("answer quietly")],
            Parent = parent,
            Signal = default,
        });

        var result = await run.Result.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(SubagentStopReason.Completed, result.StopReason);
        Assert.Equal("headless answer", string.Concat(result.Output.OfType<TextBlock>().Select(block => block.Text)));

        Assert.Equal(storeSizeAfterParent, fixture.Sessions.List().Count);
        Assert.Equal(0, sessionStarts);
        await run.DisposeAsync();
        var end = Assert.Single(ends);
        Assert.Equal(run.Id, end.Id);
        Assert.Equal(SubagentStopReason.Completed, end.StopReason);
        subscription();
        endSubscription();
    }

    [Fact]
    public async Task HeadlessAbort_StillReportsSelfContainedSnapshot()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = Fixture.Create((_, ct) => StreamOf(
            [
                new StreamChunk.BlockStart(0, "text"),
                new StreamChunk.TextDelta(0, "headless partial"),
            ],
            started, blockAfterPrefix: true, ct));
        var parent = await fixture.CreateParent("session-parent-headless-abort");
        using var cts = new CancellationTokenSource();

        var run = await fixture.Subagents.StartAsync("headless", new SubagentStartRequest
        {
            Label = "headless interruption",
            Prompt = [new TextBlock("write something long")],
            Parent = parent,
            Signal = cts.Token,
        });
        await started.Task.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var result = await run.Result.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(SubagentStopReason.Aborted, result.StopReason);
        var interruption = result.Interruption;
        Assert.NotNull(interruption);
        Assert.Equal(SubagentInterruptionPhase.LlmStream, interruption.Phase);
        Assert.Contains("headless partial", interruption.PartialOutput);
        Assert.True(interruption.Resumable);
        await run.DisposeAsync();
    }

    private static async IAsyncEnumerable<StreamChunk> GatedFirst(
        TaskCompletionSource started,
        TaskCompletionSource release,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new StreamChunk.BlockStart(0, "text");
        yield return new StreamChunk.TextDelta(0, "working");
        started.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        yield return new StreamChunk.BlockEnd(0, new TextBlock("working"));
        yield return new StreamChunk.Finish(new FinishReason.Stop());
    }

    [Fact]
    public async Task InterruptAgent_CancelsLiveChild()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = Fixture.Create((_, ct) => StreamOf(
            [
                new StreamChunk.BlockStart(0, "text"),
                new StreamChunk.TextDelta(0, "busy"),
            ],
            started, blockAfterPrefix: true, ct));
        var control = SubagentControlTools.Apply(fixture.Ctx);
        try
        {
            var parent = await fixture.CreateParent("session-parent-interrupt-live");
            var run = await fixture.Subagents.StartAsync("spawn", new SubagentStartRequest
            {
                Label = "interruptible run",
                Prompt = [new TextBlock("work until interrupted")],
                Parent = parent,
                Signal = default,
            });
            await started.Task.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);

            var result = await fixture.Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create("call-interrupt-live"),
                Name = SubagentControlTools.InterruptAgentToolName,
                Arguments = JsonDocument.Parse($$"""{"childId":"{{run.Id.Value}}"}""").RootElement,
                Agent = parent,
                Signal = default,
            });

            var accepted = Assert.IsType<ToolExecutionResult.Success>(result);
            Assert.True(accepted.Value.GetProperty("accepted").GetBoolean());
            var runResult = await run.Result.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(SubagentStopReason.Aborted, runResult.StopReason);
            await run.DisposeAsync();
        }
        finally
        {
            control.Dispose();
        }
    }

    [Fact]
    public async Task SendMessage_SteersIntoRunningChild()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        using var fixture = Fixture.Create((_, ct) =>
        {
            var request = Interlocked.Increment(ref call);
            return request == 1
                ? GatedFirst(started, release, ct)
                : StreamOf(
                    [
                        new StreamChunk.BlockStart(0, "text"),
                        new StreamChunk.TextDelta(0, "final"),
                        new StreamChunk.BlockEnd(0, new TextBlock("final")),
                        new StreamChunk.Finish(new FinishReason.Stop()),
                    ],
                    null, blockAfterPrefix: false, ct);
        });
        var control = SubagentControlTools.Apply(fixture.Ctx);
        try
        {
            var parent = await fixture.CreateParent("session-parent-steer-live");
            var run = await fixture.Subagents.StartAsync("spawn", new SubagentStartRequest
            {
                Label = "steerable run",
                Prompt = [new TextBlock("work, then listen")],
                Parent = parent,
                Signal = default,
            });
            await started.Task.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);

            var sent = await fixture.Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create("call-send-live"),
                Name = SubagentControlTools.SendMessageToolName,
                Arguments = JsonDocument.Parse(
                    $$"""{"content":"extra instruction","target":"child","childId":"{{run.Id.Value}}"}""").RootElement,
                Agent = parent,
                Signal = default,
            });
            var delivered = Assert.IsType<ToolExecutionResult.Success>(sent);
            Assert.True(delivered.Value.GetProperty("delivered").GetBoolean());

            release.SetResult();
            var result = await run.Result.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(SubagentStopReason.Completed, result.StopReason);
            Assert.Equal(2, fixture.Adapter.Requests.Count);
            Assert.Contains(fixture.Adapter.Requests[1].Messages, message =>
                message.Role == MessageRole.User
                && message.Content.OfType<TextBlock>().Any(block => block.Text == "extra instruction"));
            await run.DisposeAsync();
        }
        finally
        {
            control.Dispose();
        }
    }

    [Fact]
    public async Task SendMessage_ToHeadlessChild_UpgradesIntoSessionStore()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        using var fixture = Fixture.Create((_, ct) =>
        {
            var request = Interlocked.Increment(ref call);
            return request == 1
                ? GatedFirst(started, release, ct)
                : StreamOf(
                    [
                        new StreamChunk.BlockStart(0, "text"),
                        new StreamChunk.TextDelta(0, "upgraded final"),
                        new StreamChunk.BlockEnd(0, new TextBlock("upgraded final")),
                        new StreamChunk.Finish(new FinishReason.Stop()),
                    ],
                    null, blockAfterPrefix: false, ct);
        });
        var control = SubagentControlTools.Apply(fixture.Ctx);
        try
        {
            var parent = await fixture.CreateParent("session-parent-headless-upgrade");
            var storeSizeAfterParent = fixture.Sessions.List().Count;
            var created = new List<SessionId>();
            var subscription = fixture.Ctx.On<SessionCreatedNotification>(
                notification => created.Add(notification.Session.Id),
                new EventOptions { Global = true });

            var run = await fixture.Subagents.StartAsync("headless", new SubagentStartRequest
            {
                Label = "headless to upgrade",
                Prompt = [new TextBlock("work headless")],
                Parent = parent,
                Signal = default,
            });
            await started.Task.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(storeSizeAfterParent, fixture.Sessions.List().Count);

            var sent = await fixture.Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create("call-send-upgrade"),
                Name = SubagentControlTools.SendMessageToolName,
                Arguments = JsonDocument.Parse(
                    $$"""{"content":"now with oversight","target":"child","childId":"{{run.Id.Value}}"}""").RootElement,
                Agent = parent,
                Signal = default,
            });
            Assert.IsType<ToolExecutionResult.Success>(sent);

            Assert.Equal(storeSizeAfterParent + 1, fixture.Sessions.List().Count);
            Assert.Contains(run.Id, created);
            Assert.Equal(parent.Id, fixture.Sessions.Get(run.Id)!.Header.ParentSession);

            release.SetResult();
            var result = await run.Result.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(SubagentStopReason.Completed, result.StopReason);
            Assert.Equal(2, fixture.Adapter.Requests.Count);
            Assert.Contains(fixture.Adapter.Requests[1].Messages, message =>
                message.Role == MessageRole.User
                && message.Content.OfType<TextBlock>().Any(block => block.Text == "now with oversight"));
            await run.DisposeAsync();
        }
        finally
        {
            control.Dispose();
        }
    }

    [Fact]
    public async Task InterruptAgent_OnHeadlessChild_DoesNotUpgrade()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = Fixture.Create((_, ct) => StreamOf(
            [
                new StreamChunk.BlockStart(0, "text"),
                new StreamChunk.TextDelta(0, "busy headless"),
            ],
            started, blockAfterPrefix: true, ct));
        var control = SubagentControlTools.Apply(fixture.Ctx);
        try
        {
            var parent = await fixture.CreateParent("session-parent-headless-interrupt");
            var storeSizeAfterParent = fixture.Sessions.List().Count;
            var run = await fixture.Subagents.StartAsync("headless", new SubagentStartRequest
            {
                Label = "headless interrupt",
                Prompt = [new TextBlock("work until interrupted")],
                Parent = parent,
                Signal = default,
            });
            await started.Task.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);

            var result = await fixture.Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create("call-interrupt-headless"),
                Name = SubagentControlTools.InterruptAgentToolName,
                Arguments = JsonDocument.Parse($$"""{"childId":"{{run.Id.Value}}"}""").RootElement,
                Agent = parent,
                Signal = default,
            });
            Assert.IsType<ToolExecutionResult.Success>(result);

            var runResult = await run.Result.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(SubagentStopReason.Aborted, runResult.StopReason);
            Assert.Equal(storeSizeAfterParent, fixture.Sessions.List().Count);
            await run.DisposeAsync();
        }
        finally
        {
            control.Dispose();
        }
    }
}
