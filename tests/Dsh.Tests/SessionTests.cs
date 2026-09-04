using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tests;

public class SessionTests
{
    private static SessionId NewSessionId() => SessionId.Create(Guid.NewGuid().ToString());

    private static UserMessagePayload UserText(string text)
        => new(MessageFactory.CreateUserText(text));

    [Fact]
    public void Append_AssignsContiguousSeqs()
    {
        var session = Session.Create(NewSessionId());
        var first = session.Append(UserText("hello"), new SurfaceOp.Append());
        var second = session.Append(new TurnStartPayload(1));

        Assert.Equal(0, first.Seq);
        Assert.Equal(1, second.Seq);
        Assert.Equal(2, session.Seq);
        Assert.Same(second, session.EventAt(1));
    }

    [Fact]
    public void Append_SurfaceEligibleRequiresSurfaceOp()
    {
        var session = Session.Create(NewSessionId());
        Assert.Throws<InvalidOperationException>(() => session.Append(UserText("hello")));
    }

    [Fact]
    public void Append_NonSurfaceRejectsSurfaceOp()
    {
        var session = Session.Create(NewSessionId());
        Assert.Throws<InvalidOperationException>(() => session.Append(new TurnStartPayload(1), new SurfaceOp.Append()));
    }

    [Fact]
    public void DeriveMessages_ProjectsSurfaceNodesInOrder()
    {
        var session = Session.Create(NewSessionId());
        session.Append(UserText("q"), new SurfaceOp.Append());
        var assistant = new AssistantMessagePayload(1, 1,
            MessageFactory.CreateAssistantMessage([new TextBlock("a")], "deepseek-official", "deepseek-v4-flash"));
        session.Append(assistant, new SurfaceOp.Append(), [0]);

        var messages = session.DeriveMessages();
        Assert.Equal(2, messages.Count);
        Assert.Equal("q", Assert.IsType<TextBlock>(messages[0].Content[0]).Text);
        Assert.Equal("a", Assert.IsType<TextBlock>(messages[1].Content[0]).Text);
    }

    [Fact]
    public void DeriveMessages_SkipsEmptyAssistantMessage()
    {
        var session = Session.Create(NewSessionId());
        session.Append(new AssistantMessagePayload(1, 1,
            MessageFactory.CreateAssistantMessage([], "deepseek-official", "deepseek-v4-flash"),
            new TokenUsage(1, 0)), new SurfaceOp.Append());

        Assert.Empty(session.DeriveMessages());
    }

    [Fact]
    public void SurfaceReplace_ShadowsNodesAndBumpsGeneration()
    {
        var session = Session.Create(NewSessionId());
        session.Append(UserText("old"), new SurfaceOp.Append());
        session.Append(UserText("other"), new SurfaceOp.Append());
        var before = session.DeriveMessages();
        Assert.Equal(2, before.Count);

        session.Append(UserText("compacted"), new SurfaceOp.Replace(0, 1), [0, 1]);

        var after = session.DeriveMessages();
        var message = Assert.Single(after);
        Assert.Equal("compacted", Assert.IsType<TextBlock>(message.Content[0]).Text);
    }

    [Fact]
    public void SurfaceReplace_RequiresCompleteShadowCoverage()
    {
        var session = Session.Create(NewSessionId());
        session.Append(UserText("old"), new SurfaceOp.Append());
        session.Append(UserText("other"), new SurfaceOp.Append());

        var error = Assert.Throws<InvalidOperationException>(() =>
            session.Append(UserText("compacted"), new SurfaceOp.Replace(0, 1), [0]));
        Assert.Contains("sourceEventSeqs must include every shadowed surface node", error.Message);
    }

    [Fact]
    public void Seed_RequiresContiguousSeqsFromZero()
    {
        var events = new[]
        {
            new SessionEvent { Type = SessionEventTypes.TurnStart, Seq = 1, Time = 0, Data = new TurnStartPayload(1) },
        };
        Assert.Throws<ArgumentException>(() => Session.Create(NewSessionId(), events));
    }

    [Fact]
    public void Seed_AppendsEndSeedMarkerOnce()
    {
        var events = new[]
        {
            new SessionEvent { Type = SessionEventTypes.TurnStart, Seq = 0, Time = 0, Data = new TurnStartPayload(1) },
        };
        var session = Session.Create(NewSessionId(), events);

        Assert.Equal(2, session.Seq);
        Assert.Equal(SessionEventTypes.SessionEndSeed, session.EventAt(1)!.Type);
        Assert.Equal(1, session.FirstLiveSeq);
    }

    [Fact]
    public void RequestHeader_FoldsLatestSnapshot()
    {
        var session = Session.Create(NewSessionId());
        var config = new LlmCallConfig("deepseek-official", "deepseek-v4-flash");
        session.Append(new RequestHeaderPayload(new EpochHeader(config, System: "sys"), RequestHeaderReasons.Initial));
        session.Append(new RequestHeaderPayload(new EpochHeader(config with { MaxTokens = 1024 }, System: "sys"), RequestHeaderReasons.Change));

        var header = session.RequestHeader();
        Assert.NotNull(header);
        Assert.Equal(1024, header.Config.MaxTokens);
        Assert.Equal("sys", header.System);
    }

    [Fact]
    public void EventJson_RoundTripsSurfaceEvent()
    {
        var sessionEvent = new SessionEvent
        {
            Type = SessionEventTypes.AssistantMessage,
            Seq = 3,
            Time = 1725000000000,
            Data = new AssistantMessagePayload(1, 1,
                MessageFactory.CreateAssistantMessage(
                    [new ReasoningBlock("think"), new TextBlock("answer")],
                    "deepseek-official", "deepseek-v4-flash"),
                new TokenUsage(10, 5, 15),
                true),
            SurfaceOp = new SurfaceOp.Append(),
            SourceEventSeqs = [1, 2],
        };

        var json = JsonSerializer.Serialize(sessionEvent, DshJson.Options);
        var restored = JsonSerializer.Deserialize<SessionEvent>(json, DshJson.Options);

        Assert.NotNull(restored);
        Assert.Equal(sessionEvent.Seq, restored.Seq);
        Assert.Equal(sessionEvent.Time, restored.Time);
        var payload = Assert.IsType<AssistantMessagePayload>(restored.Data);
        Assert.Equal(2, payload.Message.Content.Count);
        Assert.Equal(new TokenUsage(10, 5, 15), payload.Usage);
        Assert.True(payload.Interrupted);
        Assert.IsType<SurfaceOp.Append>(restored.SurfaceOp);
        Assert.Equal([1L, 2L], restored.SourceEventSeqs);
    }

    [Fact]
    public void EventJson_UnknownTypeWithoutIgnorableRefuses()
    {
        const string json = """{"type":"plugin/x","seq":0,"time":0,"data":{}}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SessionEvent>(json, DshJson.Options));
    }

    [Fact]
    public void EventJson_UnknownTypeWithIgnorableIsSkipped()
    {
        const string json = """{"type":"plugin/x","seq":0,"time":0,"data":{"a":1},"ignorable":true}""";
        var restored = JsonSerializer.Deserialize<SessionEvent>(json, DshJson.Options);
        var payload = Assert.IsType<UnknownSessionEventPayload>(restored!.Data);
        Assert.Equal("plugin/x", payload.Type);
    }

    [Fact]
    public void EventJson_RejectsLegacyHeaderDelta()
    {
        const string json = """{"type":"request/header-delta","seq":0,"time":0,"data":{}}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SessionEvent>(json, DshJson.Options));
    }

    [Fact]
    public void EventJson_RejectsLegacyFallbackReason()
    {
        const string json = """{"type":"request/header","seq":0,"time":0,"data":{"header":{"config":{"provider":"p","model":"m"}},"reason":"fallback"}}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SessionEvent>(json, DshJson.Options));
    }

    [Fact]
    public void TurnEndJson_RoundTripsAllKinds()
    {
        TurnEndReason[] reasons =
        [
            new TurnEndReason.Completed(),
            new TurnEndReason.Aborted(new AgentCancelCause.Hook("hooked")),
            new TurnEndReason.Blocked(),
            new TurnEndReason.Error(new LlmFailure("boom", "SERVER", 500)),
            new TurnEndReason.MaxTokens(),
            new TurnEndReason.Interrupted(),
        ];
        foreach (var reason in reasons)
        {
            var json = JsonSerializer.Serialize(reason, DshJson.Options);
            var restored = JsonSerializer.Deserialize<TurnEndReason>(json, DshJson.Options);
            Assert.Equal(reason, restored);
        }
    }
}
