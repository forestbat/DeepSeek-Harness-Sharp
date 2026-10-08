using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

/** 模型 id 允许包含斜杠(如 kilo/anthropic/claude-3-haiku), /model 只把第一个斜杠之前当 provider。 */
public class ModelCommandTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Switches_Model_With_Multi_Segment_Id()
    {
        var (commands, agent) = await CreateAgent();

        var execution = await commands.Execute(agent, "/model multi/anthropic/claude-3-haiku", TestContext.Current.CancellationToken);

        var success = Assert.IsType<CommandResult.Success>(execution!.Result);
        Assert.Equal("switched to multi/anthropic/claude-3-haiku", success.Text);
        var config = agent.Session.RequestHeader()!.Config;
        Assert.Equal("multi", config.Provider);
        Assert.Equal("anthropic/claude-3-haiku", config.Model);
    }

    [Fact]
    public async Task Rejects_Missing_Model_Or_Provider()
    {
        var (commands, agent) = await CreateAgent();

        var missingModel = await commands.Execute(agent, "/model multi", TestContext.Current.CancellationToken);
        var missingProvider = await commands.Execute(agent, "/model /m1", TestContext.Current.CancellationToken);

        Assert.IsType<CommandResult.Error>(missingModel!.Result);
        Assert.IsType<CommandResult.Error>(missingProvider!.Result);
    }

    private async Task<(CommandsService Commands, AgentLoopAgent Agent)> CreateAgent()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var commands = CommandsService.Register(ctx);
        _ = new LlmRuntime(ctx).RegisterAdapter(["multi"], new MultiAdapter());
        _ = ModelCommand.Register(ctx, HarnessHome.Resolve(_homeDir));
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("multi", "m1")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        return (commands, agent);
    }

    private sealed class MultiAdapter : LlmAdapter
    {
        public override LlmProviderInfo ProviderInfo { get; } = new("multi", "multi");

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override LlmResolvedModelInfo ResolveModel(string model)
            => new("multi", model, model);
    }

    public void Dispose() => TempTree.Delete(_homeDir);
}
