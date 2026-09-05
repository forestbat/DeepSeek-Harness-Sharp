using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

public static class TokenEstimate
{
    public const int CharsPerToken = 4;
    public const int BlockOverhead = 4;
    public const int RoleOverhead = 4;

    public static int EstimateStructuralBlock(ContentBlock block)
        => BlockOverhead + (int)Math.Ceiling(JsonSerializer.Serialize(block, DshJson.Options).Length / (double)CharsPerToken);

    public static int EstimateContent(IReadOnlyList<ContentBlock> blocks)
    {
        var tokens = 0;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextBlock text:
                    tokens += (int)Math.Ceiling(text.Text.Length / (double)CharsPerToken) + BlockOverhead;
                    break;
                case ReasoningBlock reasoning:
                    tokens += (int)Math.Ceiling(reasoning.Text.Length / (double)CharsPerToken) + BlockOverhead;
                    break;
                case ToolCallBlock call:
                    tokens += (int)Math.Ceiling(call.Name.Length / (double)CharsPerToken)
                        + (int)Math.Ceiling(call.Arguments.Length / (double)CharsPerToken)
                        + BlockOverhead;
                    break;
                case ToolResultBlock result:
                    tokens += EstimateContent(result.Content) + BlockOverhead;
                    break;
                default:
                    tokens += EstimateStructuralBlock(block);
                    break;
            }
        }
        return tokens;
    }

    public static int EstimateMessage(Message message) => EstimateContent(message.Content) + RoleOverhead;

    public static int EstimateSystemTokens(EpochHeader? header)
        => header?.System is null ? 0 : (int)Math.Ceiling(header.System.Length / (double)CharsPerToken) + RoleOverhead;

    public static int EstimateToolsTokens(EpochHeader? header)
        => header?.Tools is not { Count: > 0 } tools
            ? 0
            : (int)Math.Ceiling(JsonSerializer.Serialize(tools, DshJson.Options).Length / (double)CharsPerToken) + BlockOverhead;

    public static int EstimateHeader(EpochHeader? header)
        => EstimateSystemTokens(header) + EstimateToolsTokens(header);
}
