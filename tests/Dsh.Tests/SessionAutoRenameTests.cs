using System.Runtime.CompilerServices;
using Dsh.Boot;
using Dsh.Compaction;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/** 首轮结束后自动命名: 用命名模型(compaction_model ?? 会话模型)生成短标题。 */
public class SessionAutoRenameTests
{
    private const string Provider = "test-provider";
    private const string Model = "test-model";
    private const string Title = "蓝色大肥鱼分析";

    private sealed class TitleAdapter : LlmAdapter
    {
        public int TitleCalls { get; private set; }

        public override LlmProviderInfo ProviderInfo => new(Provider, "Test Provider");

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override LlmResolvedModelInfo ResolveModel(string model)
            => new(Provider, model, model, ContextWindow: 100_000);

        public override async IAsyncEnumerable<StreamChunk> Stream(
            GenerateOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string text;
            if (options.Purpose == GeneratePurpose.SessionTitle)
            {
                TitleCalls++;
                text = Title;
            }
            else
            {
                text = "hello from the model";
            }
            yield return new StreamChunk.BlockStart(0, "text");
            yield return new StreamChunk.TextDelta(0, text);
            yield return new StreamChunk.BlockEnd(0, new TextBlock(text));
            yield return new StreamChunk.Finish(new FinishReason.Stop());
        }
    }

    [Fact]
    public async Task RenamesSessionAfterFirstCompletedTurn()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-autorename", Guid.NewGuid().ToString("N"));
        var home = new HarnessHome(Path.Combine(root, "home"));
        var cwd = Path.Combine(root, "project");
        Directory.CreateDirectory(home.Root);
        Directory.CreateDirectory(cwd);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(home.Root, "settings.yaml"),
                $"compaction_model: {Provider}/{Model}\n",
                TestContext.Current.CancellationToken);

            var ctx = new Context();
            _ = new SessionStore(ctx);
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            _ = new ToolRuntime(ctx);
            var llm = new LlmRuntime(ctx);
            var agents = new AgentRegistry(ctx);
            _ = new AgentLoop(ctx);
            var adapter = new TitleAdapter();
            llm.RegisterAdapter([Provider], adapter);

            var options = new HarnessOptions(home, Cwd: cwd);
            using var autoRename = new SessionAutoRename(ctx, options);

            var handle = await agents.Create(new CreateAgentOptions(
                SessionId.Create("session-rename"),
                cwd,
                new AgentOptions(Provider, Model)), TestContext.Current.CancellationToken);
            var agent = (AgentLoopAgent)handle.Agent;

            agent.Followup(MessageFactory.CreateUserText("hi"));
            await agent.WhenIdle();

            await WaitForTitleAsync(agent, Title);
            Assert.Equal(Title, agent.Session.Header.Title);
            Assert.Equal(1, adapter.TitleCalls);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public async Task FullComposition_RenamesWithSessionModelWhenNoCompactionModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-autorename-full", Guid.NewGuid().ToString("N"));
        var home = new HarnessHome(Path.Combine(root, "home"));
        var cwd = Path.Combine(root, "project");
        Directory.CreateDirectory(home.Root);
        Directory.CreateDirectory(cwd);
        try
        {
            // 与用户一致: 不设 compaction_model, 退回会话模型。
            await File.WriteAllTextAsync(
                Path.Combine(home.Root, "settings.yaml"),
                "logging:\n  file: false\n",
                TestContext.Current.CancellationToken);

            using (var app = await HarnessComposer.Compose(new HarnessOptions(home, Cwd: cwd)))
            {
                var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
                var adapter = new TitleAdapter();
                llm.RegisterAdapter([Provider], adapter);
                var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;

                var handle = await agents.Create(new CreateAgentOptions(
                    SessionId.Create("session-rename-full"),
                    cwd,
                    new AgentOptions(Provider, Model)), TestContext.Current.CancellationToken);
                var agent = (AgentLoopAgent)handle.Agent;

                agent.Followup(MessageFactory.CreateUserText("hi"));
                await agent.WhenIdle();

                await WaitForTitleAsync(agent, Title);
                Assert.Equal(Title, agent.Session.Header.Title);
                Assert.Equal(1, adapter.TitleCalls);
            }
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    /** 持久化层会先写首行兜底标题, 所以盯住模型标题有没有落定, 而不是"标题非空"。 */
    private static async Task WaitForTitleAsync(AgentLoopAgent agent, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline
            && !string.Equals(agent.Session.Header.Title, expected, StringComparison.Ordinal))
            await Task.Delay(25, TestContext.Current.CancellationToken);
    }

    /** 组合体的后台 worker 可能仍持有工作区文件句柄, 清理失败不掩盖断言结果。 */
    private static void DeleteTempDir(string root)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(root))
                    return;
                Directory.Delete(root, true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(100);
            }
        }
    }
}
