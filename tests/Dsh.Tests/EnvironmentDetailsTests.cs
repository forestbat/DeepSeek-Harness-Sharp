using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tests;

public sealed class EnvironmentDetailsTests
{
    [Fact]
    public void AppendsShortFactsToEveryUserMessage()
    {
        var id = SessionId.Create($"session-environment-{Guid.NewGuid():N}");
        var session = Session.Create(
            id,
            header: new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Cwd = @"C:\workspace",
                IsSeeded = false,
            });
        var first = MessageFactory.CreateUserText("first");
        var second = MessageFactory.CreateUserText("second");
        var firstEvent = session.Append(new UserMessagePayload(first), new SurfaceOp.Append());
        session.Append(new UserMessagePayload(second), new SurfaceOp.Append());

        var projected = EnvironmentDetails.Append(session, [first, second]);
        var firstText = Assert.IsType<TextBlock>(projected[0].Content[^1]).Text;
        var secondText = Assert.IsType<TextBlock>(projected[1].Content[^1]).Text;

        Assert.Contains(EnvironmentDetails.OpenTag, firstText);
        Assert.Contains("Message time:", firstText);
        Assert.Contains(@"Working directory: C:\workspace", firstText);
        Assert.Contains(@"Workspace root folder: C:\workspace", firstText);
        Assert.Contains(EnvironmentDetails.CloseTag, firstText);
        Assert.Contains("Message time:", secondText);

        var expectedTime = DateTimeOffset.FromUnixTimeMilliseconds(firstEvent.Time)
            .ToLocalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        Assert.Contains($"Message time: {expectedTime}", firstText);
        Assert.Equal(firstText, Assert.IsType<TextBlock>(EnvironmentDetails.Append(session, [first]).Single().Content[^1]).Text);
    }
}
