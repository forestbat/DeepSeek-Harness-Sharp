using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** §14 A: 远端会话事件以完整 SessionEvent JSON 承载, 本地据此原样重建(载荷/序号/序号依赖都不丢)。 */
public sealed class RemoteSessionEventTests
{
    [Fact]
    public void SessionEvent_SurvivesTheRemoteEventCarrier()
    {
        var payload = new UserMessagePayload(MessageFactory.CreateUserText("你好 hello"));
        var sessionEvent = new SessionEvent
        {
            Type = SessionEventTypes.UserMessage,
            Seq = 7,
            Time = 1234,
            Data = payload,
            SurfaceOp = new SurfaceOp.Append(),
        };

        var info = new RemoteEventInfo("s-1", DshJson.Serialize(sessionEvent));
        var wire = JsonSerializer.SerializeToElement(info, HostProtocolJsonContext.Default.RemoteEventInfo);
        var back = JsonSerializer.Deserialize(wire, HostProtocolJsonContext.Default.RemoteEventInfo)!;

        Assert.Equal("s-1", back.SessionId);
        var decoded = DshJson.Deserialize<SessionEvent>(back.EventJson)!;
        Assert.Equal(SessionEventTypes.UserMessage, decoded.Type);
        Assert.Equal(7, decoded.Seq);
        Assert.Equal(1234, decoded.Time);
        var user = Assert.IsType<UserMessagePayload>(decoded.Data);
        Assert.Equal("你好 hello", string.Concat(user.Message.Content.OfType<TextBlock>().Select(block => block.Text)));
    }

    [Fact]
    public void SubscribeRequest_CarriesSessionAndFromSeq()
    {
        var element = JsonSerializer.SerializeToElement(
            new RemoteSubscribeRequest("s-9", 42), HostProtocolJsonContext.Default.RemoteSubscribeRequest);
        var back = JsonSerializer.Deserialize(element, HostProtocolJsonContext.Default.RemoteSubscribeRequest)!;
        Assert.Equal("s-9", back.SessionId);
        Assert.Equal(42, back.FromSeq);
    }
}
