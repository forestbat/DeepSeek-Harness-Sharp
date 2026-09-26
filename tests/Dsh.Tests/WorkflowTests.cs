using System.Runtime.CompilerServices;
using System.Text.Json;
using Dsh.Runtime;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Subagent;
using Dsh.Workflow;

namespace Dsh.Tests;

public class WorkflowTests
{
    private sealed class ScriptedAdapter : LlmAdapter
    {
        private readonly Queue<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> _script;

        public ScriptedAdapter(string provider, IEnumerable<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> script)
        {
            _script = new Queue<Func<GenerateOptions, IReadOnlyList<StreamChunk>>>(script);
            ProviderInfo = new LlmProviderInfo(provider, provider);
        }

        public override LlmProviderInfo ProviderInfo { get; }

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
        {
            if (_script.Count == 0)
                throw new InvalidOperationException("scripted adapter: no scripted response left");
            return Yield(_script.Dequeue()(options), cancellationToken);
        }

        private static async IAsyncEnumerable<StreamChunk> Yield(
            IEnumerable<StreamChunk> chunks, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }
    }

    private sealed class WorkflowFixture : IDisposable
    {
        private readonly IDisposable _spawn;
        private readonly IDisposable _fork;
        private readonly SubprocessService _subprocess;

        public WorkflowFixture(IEnumerable<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> script)
        {
            Ctx = new Context();
            _ = new SessionStore(Ctx);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            var llm = new LlmRuntime(Ctx);
            _ = new AgentRegistry(Ctx);
            _ = new AgentLoop(Ctx);
            _ = ApprovalService.Register(Ctx);
            _ = new SubagentRuntime(Ctx);
            _subprocess = new SubprocessService(Ctx);
            llm.RegisterAdapter(["test-provider"], new ScriptedAdapter("test-provider", script));
            _spawn = SubagentInProcessProviders.RegisterSpawn(Ctx);
            _fork = SubagentInProcessProviders.RegisterFork(Ctx);
            Workflow = new WorkerThreadWorkflowEngine(Ctx, null);
        }

        public Context Ctx { get; }
        public ToolRuntime Tools { get; }
        public WorkflowEngine Workflow { get; }

        public async Task<AgentLoopAgent> CreateParent(string id)
        {
            var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var handle = await agents.Create(new CreateAgentOptions(
                SessionId.Create(id), null, new AgentOptions("test-provider", "test-model")));
            return (AgentLoopAgent)handle.Agent;
        }

        public void Dispose()
        {
            _spawn.Dispose();
            _fork.Dispose();
            _subprocess.Dispose();
        }
    }

    private static IReadOnlyList<StreamChunk> TextAnswer(string text) =>
    [
        new StreamChunk.BlockStart(0, "text"),
        new StreamChunk.TextDelta(0, text),
        new StreamChunk.BlockEnd(0, new TextBlock(text)),
        new StreamChunk.Finish(new FinishReason.Stop()),
    ];

    private static IReadOnlyList<StreamChunk> ToolCallAnswer(string callId, string name, string arguments) =>
    [
        new StreamChunk.ToolCallDelta(0, ToolCallId.Create(callId), name, arguments),
        new StreamChunk.Finish(new FinishReason.ToolCalls()),
    ];

    private static Dictionary<string, object?> Meta(string name, string description) => new()
    {
        ["name"] = name,
        ["description"] = description,
    };

    [Fact]
    public async Task Engine_RunsAgentsAndReturnsMaterializedJson()
    {
        using var fixture = new WorkflowFixture(
        [
            _ => TextAnswer("hello one"),
            _ => TextAnswer("hello two"),
        ]);
        var parent = await fixture.CreateParent("session-parent-workflow");
        var run = fixture.Workflow.Start(new WorkflowStartRequest
        {
            Script = """
                var a = await agent("child one");
                await phase("phase1");
                var b = await agent("child two", new JsonObject { ["label"] = "two" });
                return new JsonObject { ["a"] = a, ["b"] = b, ["count"] = 2 };
                """,
            Meta = Meta("test", "test description"),
            Parent = parent,
        });

        var result = await run.Result;

        Assert.Equal(WorkflowStopReason.Completed, result.StopReason);
        Assert.Equal(2, result.AgentsStarted);
        var value = Assert.IsType<Dictionary<string, object?>>(result.Value);
        Assert.Equal("hello one", value["a"]);
        Assert.Equal("hello two", value["b"]);
        Assert.Equal(2.0, value["count"]);
    }

    [Fact]
    public async Task Engine_RunsParallelAndPipeline()
    {
        using var fixture = new WorkflowFixture([]);
        var parent = await fixture.CreateParent("session-parent-workflow-parallel");
        var run = fixture.Workflow.Start(new WorkflowStartRequest
        {
            Script = """
                var par = await parallel(new Func<Task<JsonNode?>>[]
                {
                    () => Task.FromResult<JsonNode?>(JsonValue.Create(1)),
                    () => Task.FromResult<JsonNode?>(JsonValue.Create(2)),
                });
                var pipe = await pipeline(
                    new JsonArray(JsonValue.Create(1), JsonValue.Create(2)),
                    async (prev, item, index) => JsonValue.Create(prev!.GetValue<int>() + item!.GetValue<int>()),
                    async (prev, item, index) => JsonValue.Create(prev!.GetValue<int>() * 2));
                return new JsonObject { ["par"] = par, ["pipe"] = pipe };
                """,
            Meta = Meta("parallel-test", "parallel and pipeline test"),
            Parent = parent,
        });

        var result = await run.Result;

        Assert.Equal(WorkflowStopReason.Completed, result.StopReason);
        var value = Assert.IsType<Dictionary<string, object?>>(result.Value);
        var par = Assert.IsAssignableFrom<List<object?>>(value["par"]);
        Assert.Equal(1.0, par[0]);
        Assert.Equal(2.0, par[1]);
        var pipe = Assert.IsAssignableFrom<List<object?>>(value["pipe"]);
        Assert.Equal(4.0, pipe[0]);
        Assert.Equal(8.0, pipe[1]);
    }

    [Fact]
    public async Task ToolWorkflow_ExecutesThroughToolRuntime()
    {
        using var fixture = new WorkflowFixture(
        [
            _ => TextAnswer("tool one"),
            _ => TextAnswer("tool two"),
        ]);
        _ = ToolWorkflow.Apply(fixture.Ctx, new ToolWorkflowConfig());
        var parent = await fixture.CreateParent("session-parent-tool");
        var arguments = """
            {
              "script": "var a = await agent(\"child one\"); var b = await agent(\"child two\"); return new JsonObject { [\"a\"] = a, [\"b\"] = b };",
              "meta": { "name": "tool-test", "description": "tool test description" }
            }
            """;
        var result = await fixture.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-workflow"),
            Name = "workflow",
            Arguments = JsonDocument.Parse(arguments).RootElement,
            Agent = parent,
            Signal = default,
        });

        Assert.False(result.IsError);
        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Equal(2, success.Value.GetProperty("agentsStarted").GetInt32());
        Assert.Equal("tool one", success.Value.GetProperty("result").GetProperty("a").GetString());
        Assert.Equal("tool two", success.Value.GetProperty("result").GetProperty("b").GetString());
        var recordTypes = parent.Session.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Type)
            .Where(type => type.StartsWith("tool-workflow/", StringComparison.Ordinal))
            .ToList();
        Assert.Contains("tool-workflow/run-start", recordTypes);
        Assert.Contains("tool-workflow/run-end", recordTypes);
    }

    [Fact]
    public async Task Engine_ReportsNonCompilingScript()
    {
        using var fixture = new WorkflowFixture([]);
        var parent = await fixture.CreateParent("session-parent-workflow-compile");
        var error = Assert.Throws<WorkflowError>(() => fixture.Workflow.Start(new WorkflowStartRequest
        {
            Script = "this is not valid C# ;;;",
            Meta = Meta("bad", "a script that does not compile"),
            Parent = parent,
        }));

        Assert.Contains("does not compile", error.Message);
    }

    [Fact]
    public async Task ToolRalph_RunsStructuredFreshRound()
    {
        using var fixture = new WorkflowFixture(
        [
            _ => ToolCallAnswer("call-ralph-1", "structured_output",
                """{"status":"complete","summary":"objective done","evidence":["workspace verified"],"nextSteps":[],"blocker":""}"""),
        ]);
        _ = ToolRalph.Apply(fixture.Ctx, new ToolRalphConfig { SubagentProvider = "spawn", MaxRounds = 1, MaxResultChars = 16_384 });
        var parent = await fixture.CreateParent("session-parent-ralph");
        var result = await fixture.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-ralph"),
            Name = "ralph",
            Arguments = JsonDocument.Parse("""{"objective":"finish the objective","maxRounds":1}""").RootElement,
            Agent = parent,
            Signal = default,
        });

        Assert.False(result.IsError, TextOf(result));
        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Equal(1, success.Value.GetProperty("agentsStarted").GetInt32());
        Assert.Equal("complete", success.Value.GetProperty("result").GetProperty("status").GetString());
    }

    private static string TextOf(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));
}
