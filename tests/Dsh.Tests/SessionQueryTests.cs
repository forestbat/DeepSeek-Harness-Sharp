using Dsh.Core;
using Dsh.Llm;
using Dsh.SessionQuery;

namespace Dsh.Tests;

public sealed class SessionQueryTests
{
    [Fact]
    public void IndexesAndSearchesSessionEvents()
    {
        var session = Session.Create(SessionId.Create("s1"));
        session.Append(new TurnStartPayload(1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("hello world")), new SurfaceOp.Append());
        session.Append(new AssistantMessagePayload(
            1,
            1,
            MessageFactory.CreateAssistantMessage([new TextBlock("the quick brown fox")], "mock", "mock")), new SurfaceOp.Append());
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        using var index = new SessionQueryIndex();
        index.IndexSession(session);

        var hits = index.Search("quick");
        Assert.Single(hits);
        Assert.Equal("s1", hits[0].SessionId);
        Assert.Contains("quick", hits[0].Snippet);
    }
}
