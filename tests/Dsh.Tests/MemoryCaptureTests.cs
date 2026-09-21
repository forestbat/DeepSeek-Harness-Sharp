using System.Runtime.CompilerServices;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Memory;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class MemoryCaptureTests
{
    private const string Provider = "test-provider";
    private const string Model = "test-model";

    private sealed class MockCaptureAdapter : LlmAdapter
    {
        public int DigestCalls { get; private set; }

        public int OpsCalls { get; private set; }

        public override LlmProviderInfo ProviderInfo => new(Provider, "Test Provider");

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override async IAsyncEnumerable<StreamChunk> Stream(
            GenerateOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string text;
            if (options.Purpose == GeneratePurpose.Memory
                && options.Messages[0].Content[0] is TextBlock prompt
                && prompt.Text.Contains("TOPIC:"))
            {
                DigestCalls++;
                text = "TOPIC: refactor\nSUMMARY: Restructured the memory layer.";
            }
            else if (options.Purpose == GeneratePurpose.Memory)
            {
                OpsCalls++;
                text = """{"operations":[{"op":"upsert","section":"Facts","key":"repo.layout","text":"src holds the preset plugin packages"}]}""";
            }
            else
            {
                text = "ok";
            }
            yield return new StreamChunk.BlockStart(0, "text");
            yield return new StreamChunk.TextDelta(0, text);
            yield return new StreamChunk.BlockEnd(0, new TextBlock(text));
            yield return new StreamChunk.Finish(new FinishReason.Stop());
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TurnEndCompleted_TriggersDigestAndConsolidation()
    {
        using var fixture = new Fixture(memoryEnabled: true);
        var session = fixture.NewSession("capture-1");

        fixture.AppendTurn(session);
        await WaitFor(() => File.Exists(fixture.MemoryPath)
            && File.ReadAllText(fixture.MemoryPath).Contains("repo.layout"));

        Assert.Equal(1, fixture.Adapter.DigestCalls);
        Assert.Equal(1, fixture.Adapter.OpsCalls);
        var digest = File.ReadAllText(Path.Combine(fixture.SidecarDir, "sessions", "capture-1.md"));
        Assert.Contains("- topic :: refactor", digest);
        var memoryText = File.ReadAllText(fixture.MemoryPath);
        Assert.Contains("- repo.layout :: src holds the preset plugin packages (", memoryText);
        var audit = File.ReadAllText(Path.Combine(fixture.SidecarDir, "decisions.jsonl"));
        Assert.Contains("\"source\":\"capture\"", audit);
    }

    [Fact]
    public async Task TurnEndCompleted_WhenDisabled_SkipsCapture()
    {
        using var fixture = new Fixture(memoryEnabled: false);
        var session = fixture.NewSession("capture-off");

        fixture.AppendTurn(session);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.Adapter.DigestCalls);
        Assert.False(File.Exists(fixture.MemoryPath));
        Assert.False(Directory.Exists(fixture.SidecarDir));
    }

    [Fact]
    public async Task TurnEndCompleted_WithinMinInterval_CapturesOnce()
    {
        using var fixture = new Fixture(memoryEnabled: true);
        var session = fixture.NewSession("capture-rate");

        fixture.AppendTurn(session);
        fixture.AppendTurn(session, turn: 2);
        await WaitFor(() => File.Exists(fixture.MemoryPath)
            && File.ReadAllText(fixture.MemoryPath).Contains("repo.layout"));
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Adapter.DigestCalls);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition was not met within 15 seconds");
            await Task.Delay(50);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _cwd;
        private readonly SessionStore _sessions;
        private readonly MemoryCapture _capture;

        public Fixture(bool memoryEnabled)
        {
            _root = Path.Combine(Path.GetTempPath(), $"dsh-capture-{Guid.NewGuid():N}");
            _cwd = Path.Combine(_root, "project");
            Directory.CreateDirectory(_cwd);
            var home = new HarnessHome(Path.Combine(_root, "home"));
            Directory.CreateDirectory(home.Root);
            File.WriteAllText(Path.Combine(home.Root, "settings.yaml"), $"""
                memory:
                  enabled: {(memoryEnabled ? "true" : "false")}
                """);
            var ctx = new Context();
            _sessions = new SessionStore(ctx);
            var llm = new LlmRuntime(ctx);
            Adapter = new MockCaptureAdapter();
            llm.RegisterAdapter([Provider], Adapter);
            MemoryPath = Path.Combine(_cwd, ".dsh-memory.md");
            SidecarDir = Path.Combine(_cwd, ".dsh-memory");
            var memory = new ProjectMemory(new FileMemoryStore(MemoryPath), SidecarDir);
            _capture = new MemoryCapture(ctx, memory, new HarnessOptions(home, Cwd: _cwd));
        }

        public MockCaptureAdapter Adapter { get; }

        public string MemoryPath { get; }

        public string SidecarDir { get; }

        public Session NewSession(string id)
        {
            var sessionId = SessionId.Create(id);
            var session = _sessions.Create(sessionId, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = sessionId,
                CreatedAt = 0,
                Cwd = _cwd,
                IsSeeded = false,
            });
            session.Append(new RequestHeaderPayload(new EpochHeader(new LlmCallConfig(Provider, Model)), RequestHeaderReasons.Initial));
            return session;
        }

        public void AppendTurn(Session session, int turn = 1)
        {
            session.Append(new TurnStartPayload(turn));
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText("restructure the memory layer")), new SurfaceOp.Append());
            session.Append(new AssistantMessagePayload(
                turn,
                1,
                MessageFactory.CreateAssistantMessage([new TextBlock("done")], Provider, Model)),
                new SurfaceOp.Append());
            session.Append(new TurnEndPayload(turn, new TurnEndReason.Completed()));
        }

        public void Dispose()
        {
            _capture.Dispose();
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
    }
}
