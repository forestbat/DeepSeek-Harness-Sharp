using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Host;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/** 模型选择来源: 会话已有的请求头(/model 等)优先于 agent 创建时的 Options, 首轮与中途切换都成立。 */
public class AgentModelSelectionTests
{
    private sealed class RecordingAdapter(string provider, LlmModelReasoningInfo? reasoning = null) : LlmAdapter
    {
        public ConcurrentQueue<(string Provider, string Model)> Calls { get; } = new();
        public ConcurrentQueue<GenerateOptions> AgentRequests { get; } = new();

        public override LlmProviderInfo ProviderInfo { get; } = new(provider, provider);

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override LlmResolvedModelInfo ResolveModel(string model)
            => new(provider, model, model, ContextWindow: 100_000, Reasoning: reasoning);

        public override async IAsyncEnumerable<StreamChunk> Stream(
            GenerateOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Enqueue((options.Provider, options.Model));
            if (AgentLoopRequestMarker.IsMarked(options))
                AgentRequests.Enqueue(options);
            yield return new StreamChunk.BlockStart(0, "text");
            yield return new StreamChunk.TextDelta(0, "ok");
            yield return new StreamChunk.BlockEnd(0, new TextBlock("ok"));
            yield return new StreamChunk.Finish(new FinishReason.Stop());
        }
    }

    private static (LlmRuntime Llm, AgentRegistry Agents) BuildHost()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        var llm = new LlmRuntime(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        return (llm, agents);
    }

    private static void SelectModel(AgentLoopAgent agent, string provider, string model, ReasoningEffortId? reasoningEffort = null)
        => agent.Session.Append(new RequestHeaderPayload(
            RequestHeader.Canonicalize(new EpochHeader(new LlmCallConfig(provider, model, reasoningEffort))),
            RequestHeaderReasons.Change,
            true));

    [Fact]
    public async Task SessionHeader_OverridesAgentOptions_OnFirstRequest()
    {
        var (llm, agents) = BuildHost();
        var optionsAdapter = new RecordingAdapter("options-provider");
        var sessionAdapter = new RecordingAdapter("session-provider");
        llm.RegisterAdapter(["options-provider"], optionsAdapter);
        llm.RegisterAdapter(["session-provider"], sessionAdapter);

        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create("session-model-first"),
            null,
            new AgentOptions("options-provider", "options-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;
        SelectModel(agent, "session-provider", "session-model");

        agent.Followup(MessageFactory.CreateUserText("hi"));
        await agent.WhenIdle();

        Assert.Equal([("session-provider", "session-model")], sessionAdapter.Calls);
        Assert.Empty(optionsAdapter.Calls);
    }

    [Fact]
    public async Task MidSessionModelSwitch_IsHonored()
    {
        var (llm, agents) = BuildHost();
        var firstAdapter = new RecordingAdapter("first-provider");
        var secondAdapter = new RecordingAdapter("second-provider");
        llm.RegisterAdapter(["first-provider"], firstAdapter);
        llm.RegisterAdapter(["second-provider"], secondAdapter);

        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create("session-model-mid"),
            null,
            new AgentOptions("first-provider", "first-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;

        agent.Followup(MessageFactory.CreateUserText("hi"));
        await agent.WhenIdle();
        Assert.Equal([("first-provider", "first-model")], firstAdapter.Calls);

        SelectModel(agent, "second-provider", "second-model");
        agent.Followup(MessageFactory.CreateUserText("again"));
        await agent.WhenIdle();

        Assert.Equal([("second-provider", "second-model")], secondAdapter.Calls);
        Assert.Single(firstAdapter.Calls);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public async Task HostSessions_UseConfiguredDefaultsWithoutOverridingSessionHeader(bool resume, bool configured, bool sessionSelection)
    {
        var root = TempTree.CreateDirectory("host-model-selection");
        var home = HarnessHome.Resolve(root);
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(home.SettingsFile,
                configured ? "logging:\n  file: false\nglobal_default_model: host-provider/host-model\n" : GuiTestEnvironment.HomeSettings,
                cancellationToken);
            using var app = await HarnessComposer.Compose(new HarnessOptions(home, Cwd: root, ReasoningEffort: configured ? "high" : null));
            var reasoning = new LlmModelReasoningInfo(
                [new(ReasoningEffortId.Create("low"), "Low"), new(ReasoningEffortId.Create("high"), "High")]);
            var defaultsAdapter = new RecordingAdapter("host-provider", reasoning);
            var selectedAdapter = new RecordingAdapter("session-provider", reasoning);
            var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
            using var defaultsRegistration = llm.RegisterAdapter(["host-provider"], defaultsAdapter);
            using var selectedRegistration = llm.RegisterAdapter(["session-provider"], selectedAdapter);
            await using var backend = await HarnessRemoteHostBackend.CreateAsync(app, HostPtyDaemonForTests.Unused, cancellationToken);
            var created = await backend.Session.CreateSessionAsync(root, cancellationToken);
            var id = SessionId.Create(created.Id);
            var registry = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var agent = Assert.IsType<AgentLoopAgent>(registry.Get(id));
            await agent.WhenIdle();
            await backend.SessionEditing.RenameSessionAsync(created.Id, "Host model selection regression", cancellationToken);
            if (sessionSelection)
                SelectModel(agent, "session-provider", "session-model", ReasoningEffortId.Create("low"));
            if (resume)
            {
                await app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(agent.Session);
                await backend.Agent.StopAgentAsync(created.Id, cancellationToken);
                Assert.Null(registry.Get(id));
                await backend.Session.ResumeSessionAsync(created.Id, cancellationToken);
                var resumed = Assert.IsType<AgentLoopAgent>(registry.Get(id));
                Assert.NotSame(agent, resumed);
                agent = resumed;
                await agent.WhenIdle();
            }

            await backend.Session.SendMessageAsync(created.Id, "Describe the configured route.", [], cancellationToken);
            await agent.WhenIdle();
            var snapshot = await backend.Session.GetSessionSnapshotAsync(created.Id, cancellationToken);
            var payloads = snapshot.Events.Select(sessionEvent => sessionEvent.Data).ToArray();
            var turn = Assert.Single(payloads.OfType<TurnEndPayload>());
            if (!configured)
            {
                var error = Assert.IsType<TurnEndReason.Error>(turn.Reason);
                Assert.Contains("no LLM provider/model is configured", error.Failure.Message, StringComparison.Ordinal);
                Assert.Empty(defaultsAdapter.Calls);
                Assert.Empty(selectedAdapter.Calls);
                Assert.Empty(payloads.OfType<RequestHeaderPayload>());
                Assert.Empty(payloads.OfType<AssistantMessagePayload>());
                return;
            }

            Assert.IsType<TurnEndReason.Completed>(turn.Reason);
            var expectedProvider = sessionSelection ? "session-provider" : "host-provider";
            var expectedModel = sessionSelection ? "session-model" : "host-model";
            ReasoningEffortId? expectedEffort = ReasoningEffortId.Create(sessionSelection ? "low" : "high");
            var usedAdapter = sessionSelection ? selectedAdapter : defaultsAdapter;
            Assert.NotEmpty(usedAdapter.Calls);
            Assert.All(usedAdapter.Calls, call => Assert.Equal((expectedProvider, expectedModel), call));
            Assert.NotEmpty(usedAdapter.AgentRequests);
            Assert.All(usedAdapter.AgentRequests, request =>
            {
                Assert.Equal(id, request.SessionId);
                Assert.Equal((expectedProvider, expectedModel, expectedEffort), (request.Provider, request.Model, request.ReasoningEffort));
            });
            Assert.Empty((sessionSelection ? defaultsAdapter : selectedAdapter).Calls);
            var config = payloads.OfType<RequestHeaderPayload>().Last().Header.Config;
            Assert.Equal((expectedProvider, expectedModel, expectedEffort), (config.Provider, config.Model, config.ReasoningEffort));
            var assistant = Assert.Single(payloads.OfType<AssistantMessagePayload>());
            Assert.Equal("ok", string.Concat(assistant.Message.Content.OfType<TextBlock>().Select(block => block.Text)));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }
}
