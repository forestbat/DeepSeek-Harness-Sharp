using System.Runtime.CompilerServices;
using System.Text.Json;
using Cordis;
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

        public List<GenerateOptions> Requests { get; } = [];

        public override LlmProviderInfo ProviderInfo { get; }

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
        {
            Requests.Add(options);
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
            Subagents = new SubagentRuntime(Ctx);
            Adapter = new ScriptedAdapter("test-provider", script);
            llm.RegisterAdapter(["test-provider"], Adapter);
            _spawn = SubagentInProcessProviders.RegisterSpawn(Ctx);
            _fork = SubagentInProcessProviders.RegisterFork(Ctx);
            Workflow = new WorkerThreadWorkflowEngine(Ctx, null);
        }

        public Context Ctx { get; }
        public ToolRuntime Tools { get; }
        public SubagentRuntime Subagents { get; }
        public ScriptedAdapter Adapter { get; }
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
        }
    }

    private static IReadOnlyList<StreamChunk> TextAnswer(string text) =>
    [
        new StreamChunk.BlockStart(0, "text"),
        new StreamChunk.TextDelta(0, text),
        new StreamChunk.BlockEnd(0, new TextBlock(text)),
        new StreamChunk.Finish(new FinishReason.Stop()),
    ];

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
                const a = await agent('child one');
                phase('phase1');
                const b = await agent('child two', { label: 'two' });
                return { a, b, count: 2 };
                """,
            Meta = new Dictionary<string, object?>
            {
                ["name"] = "test",
                ["description"] = "test description",
            },
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
                const par = await parallel([async () => 1, () => 2]);
                const pipe = await pipeline([1, 2], (prev, item) => prev + item, (prev) => prev * 2);
                return { par, pipe };
                """,
            Meta = new Dictionary<string, object?>
            {
                ["name"] = "parallel-test",
                ["description"] = "parallel and pipeline test",
            },
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
              "script": "const a = await agent('child one'); const b = await agent('child two'); return { a, b };",
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
}