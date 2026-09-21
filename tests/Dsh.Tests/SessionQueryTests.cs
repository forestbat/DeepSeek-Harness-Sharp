using System.Text.Json;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Runtime;
using Dsh.SessionQuery;

namespace Dsh.Tests;

public sealed class SessionQueryTests
{
    [Fact]
    public void IndexesAndSearchesSessionEvents()
    {
        var session = CreateSession("s1", "hello world", "the quick brown fox");

        using var index = new SessionQueryIndex();
        index.IndexSession(session);

        var hits = index.Search("quick");
        Assert.Single(hits);
        Assert.Equal("s1", hits[0].SessionId);
        Assert.Contains("quick", hits[0].Snippet);
    }

    [Fact]
    public void Search_QuotesFtsSyntaxCharacters()
    {
        var session = CreateSession("s2", "hello world", "the quick -brown \"fox\" OR wolf");
        using var index = new SessionQueryIndex();
        index.IndexSession(session);

        var hits = index.Search("-brown \"fox\"");

        Assert.NotEmpty(hits);
    }

    [Fact]
    public void Service_IndexesLiveAndPersistedSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-session-query-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var stored = CreateSession("stored-1", "persisted keyword zebra", "answer");
            using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
            using (var handle = persistence.Create(stored.Header))
            {
                handle.Append(stored.SnapshotEvents());
                handle.Flush();
            }

            var ctx = new Context();
            ctx.Provide("sessionPersistence", persistence);
            var store = new SessionStore(ctx);
            var live = store.Create(SessionId.Create("live-1"));
            live.Append(new UserMessagePayload(MessageFactory.CreateUserText("live keyword zebra")), new SurfaceOp.Append());

            using var service = new SessionQueryService(ctx);
            var hits = service.Search("zebra");

            Assert.Contains(hits, hit => hit.SessionId == "stored-1");
            Assert.Contains(hits, hit => hit.SessionId == "live-1");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Tool_ExecutesSearch()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        var store = new SessionStore(ctx);
        var session = store.Create(SessionId.Create("tool-1"));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("tool keyword giraffe")), new SurfaceOp.Append());
        using var service = new SessionQueryService(ctx);
        using var tool = ToolSessionQueryTool.Register(ctx, 20);

        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-1"),
            Name = "session_search",
            Arguments = JsonDocument.Parse("""{"query":"giraffe"}""").RootElement,
            Signal = default,
        });

        Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Contains("tool-1", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
    }

    [Fact]
    public async Task Command_SearchesHistory()
    {
        var ctx = new Context();
        _ = new AgentRegistry(ctx);
        var commands = CommandsService.Register(ctx);
        var store = new SessionStore(ctx);
        var session = store.Create(SessionId.Create("cmd-1"));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("command keyword otter")), new SurfaceOp.Append());
        using var service = new SessionQueryService(ctx);
        using var command = SessionsCommand.Register(ctx, service);
        var agent = new FakeAgent(ctx, session);

        var hit = await commands.Execute(agent, "/sessions otter", TestContext.Current.CancellationToken);
        var usage = await commands.Execute(agent, "/sessions", TestContext.Current.CancellationToken);

        var success = Assert.IsType<CommandResult.Success>(hit?.Result);
        Assert.Contains("cmd-1", success.Text ?? "");
        Assert.IsType<CommandResult.Error>(usage?.Result);
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(Context ctx, Session session)
        {
            Ctx = ctx;
            Session = session;
        }

        public SessionId Id => Session.Id;

        public Session Session { get; }

        public ScopeKey ScopeKey { get; } = new();

        public Context Ctx { get; }

        public AgentStatus Status => AgentStatus.Idle;

        public AgentOptions Options { get; } = new();

        public void Cancel(AgentCancelCause cause, bool keepInbox = false)
        {
        }

        public Task WhenIdle() => Task.CompletedTask;

        public void Send(UserMessage message, string target, bool wakeup)
        {
        }

        public void Followup(UserMessage message)
        {
        }

        public void Steer(UserMessage message)
        {
        }

        public void Inject(UserMessage message)
        {
        }
    }

    private static Session CreateSession(string id, string userText, string assistantText)
    {
        var session = Session.Create(SessionId.Create(id));
        session.Append(new TurnStartPayload(1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText(userText)), new SurfaceOp.Append());
        session.Append(new AssistantMessagePayload(
            1,
            1,
            MessageFactory.CreateAssistantMessage([new TextBlock(assistantText)], "mock", "mock")), new SurfaceOp.Append());
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        return session;
    }
}
