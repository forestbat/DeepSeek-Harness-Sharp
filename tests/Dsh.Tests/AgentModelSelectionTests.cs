using System.Runtime.CompilerServices;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/** 模型选择来源: 会话已有的请求头(/model 等)优先于 agent 创建时的 Options, 首轮与中途切换都成立。 */
public class AgentModelSelectionTests
{
    private sealed class RecordingAdapter(string provider) : LlmAdapter
    {
        public List<(string Provider, string Model)> Calls { get; } = [];

        public override LlmProviderInfo ProviderInfo { get; } = new(provider, provider);

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override LlmResolvedModelInfo ResolveModel(string model)
            => new(provider, model, model, ContextWindow: 100_000);

        public override async IAsyncEnumerable<StreamChunk> Stream(
            GenerateOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add((options.Provider, options.Model));
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

    private static void SelectModel(AgentLoopAgent agent, string provider, string model)
        => agent.Session.Append(new RequestHeaderPayload(
            RequestHeader.Canonicalize(new EpochHeader(new LlmCallConfig(provider, model))),
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
}
