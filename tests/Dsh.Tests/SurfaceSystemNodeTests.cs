using Dsh.Core;
using Dsh.Llm;
using Dsh.Agent;

namespace Dsh.Tests;

public sealed class SurfaceSystemNodeTests
{
    private static Session NewSession() => Session.Create(SessionId.Create($"session-surface-{Guid.NewGuid():N}"));

    private static SessionEvent AppendSystem(Session session, string text)
        => session.Append(
            new SystemMessagePayload(0, 0, MessageFactory.CreateSystemMessage(
                text.Length == 0 ? [] : [new TextBlock(text)],
                new PluginMessageSource(SystemPromptProjection.Source))),
            new SurfaceOp.Append());

    [Fact]
    public void SystemHeadRewriteBySystemMessageAccepted()
    {
        var session = NewSession();
        var head = AppendSystem(session, "v1");

        var replacement = session.Append(
            new SystemMessagePayload(1, 1, MessageFactory.CreateSystemMessage(
                [new TextBlock("v2")],
                new PluginMessageSource(SystemPromptProjection.Source))),
            new SurfaceOp.Replace(head.Seq, head.Seq),
            [head.Seq]);

        Assert.Equal(replacement.Seq, session.SurfaceManager.Nodes[0]);
        Assert.Equal("v2", Assert.IsType<TextBlock>(
            Assert.IsAssignableFrom<Message>(Surface.DeriveEventMessage(session.EventAt(replacement.Seq)!)).Content[0]).Text);
    }

    [Fact]
    public void NonSystemReplaceOfSystemHeadRejected()
    {
        var session = NewSession();
        var head = AppendSystem(session, "v1");
        var next = session.Append(new UserMessagePayload(MessageFactory.CreateUserText("hi")), new SurfaceOp.Append());

        var error = Assert.Throws<InvalidOperationException>(() =>
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText("overwrite")),
                new SurfaceOp.Replace(head.Seq, head.Seq),
                [head.Seq]));
        Assert.Contains("node 0", error.Message);
    }

    [Fact]
    public void RangeReplaceCoveringSystemHeadRejected()
    {
        var session = NewSession();
        var head = AppendSystem(session, "v1");
        var next = session.Append(new UserMessagePayload(MessageFactory.CreateUserText("hi")), new SurfaceOp.Append());

        Assert.Throws<InvalidOperationException>(() =>
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText("compact")),
                new SurfaceOp.Replace(head.Seq, next.Seq),
                [head.Seq, next.Seq]));
    }

    [Fact]
    public void SystemMessageWithNonSystemRoleRejected()
    {
        var session = NewSession();
        Assert.Throws<InvalidOperationException>(() =>
            session.Append(
                new SystemMessagePayload(0, 0, MessageFactory.CreateUserText("not a system message")),
                new SurfaceOp.Append()));
    }

    [Fact]
    public void SystemMessageWithNonPluginSourceRejected()
    {
        var session = NewSession();
        Assert.Throws<InvalidOperationException>(() =>
            session.Append(
                new SystemMessagePayload(0, 0, new Message
                {
                    Id = MessageFactory.NewId(),
                    Role = MessageRole.System,
                    Content = [new TextBlock("v1")],
                    Source = new UserMessageSource(),
                }),
                new SurfaceOp.Append()));
    }

    [Fact]
    public void EmptySystemNodeDerivesNoMessage()
    {
        var session = NewSession();
        AppendSystem(session, "");
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("hi")), new SurfaceOp.Append());

        var messages = session.DeriveMessages();
        var single = Assert.Single(messages);
        Assert.IsType<UserMessage>(single);
    }

    [Fact]
    public void SystemMessageSurvivesRoundTrip()
    {
        var session = NewSession();
        AppendSystem(session, "v1");
        var sessionEvent = session.EventAt(0)!;

        var json = System.Text.Json.JsonSerializer.Serialize(sessionEvent, DshJson.Options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<SessionEvent>(json, DshJson.Options)!;

        var payload = Assert.IsType<SystemMessagePayload>(restored.Data);
        Assert.Equal("v1", Assert.IsType<TextBlock>(payload.Message.Content[0]).Text);
        Assert.Equal(sessionEvent.SurfaceOp, restored.SurfaceOp);
    }
}
