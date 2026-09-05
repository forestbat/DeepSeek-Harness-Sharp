using System.Text;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Subagent;

public sealed record AssistantOutputFold(AssistantMessage? Message, string Text);

public static class AssistantOutput
{
    public static AssistantOutputFold Fold(IReadOnlyList<SessionEvent> events)
    {
        AssistantMessage? message = null;
        var text = new StringBuilder();
        foreach (var sessionEvent in events)
        {
            switch (sessionEvent.Data)
            {
                case AssistantMessagePayload payload when payload.Message.Content.Count > 0:
                    message = payload.Message;
                    break;
                case AssistantChunkPayload { Chunk: StreamChunk.TextDelta delta }:
                    text.Append(delta.Text);
                    break;
            }
        }
        return new AssistantOutputFold(message, text.ToString());
    }

    public static IReadOnlyList<ContentBlock>? FinalAssistantOutput(IReadOnlyList<SessionEvent> events)
    {
        var fold = Fold(events);
        if (fold.Message is not null)
            return fold.Message.Content;
        return fold.Text.Length > 0 ? [new TextBlock(fold.Text)] : null;
    }
}
