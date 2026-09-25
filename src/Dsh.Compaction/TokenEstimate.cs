using Dsh.Core;
using Dsh.Llm;
using Microsoft.ML.Tokenizers;

namespace Dsh.Compaction;

public static class TokenEstimate
{
    public const int BlockOverhead = 4;
    public const int RoleOverhead = 4;

    private const int FallbackCharsPerToken = 4;
    private const string EncodingName = "o200k_base";

    private static readonly Lazy<Tokenizer?> SharedTokenizer = new(CreateTokenizer);

    private static Tokenizer? CreateTokenizer()
    {
        try
        {
            return TiktokenTokenizer.CreateForEncoding(EncodingName);
        }
        catch
        {
            return null;
        }
    }

    private static int CountText(string text)
        => SharedTokenizer.Value is { } tokenizer
            ? tokenizer.CountTokens(text)
            : (int)Math.Ceiling(text.Length / (double)FallbackCharsPerToken);

    public static int EstimateStructuralBlock(ContentBlock block)
        => BlockOverhead + CountText(DshJson.Serialize(block));

    public static int EstimateContent(IReadOnlyList<ContentBlock> blocks)
    {
        var tokens = 0;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextBlock text:
                    tokens += CountText(text.Text) + BlockOverhead;
                    break;
                case ReasoningBlock reasoning:
                    tokens += CountText(reasoning.Text) + BlockOverhead;
                    break;
                case ToolCallBlock call:
                    tokens += CountText(call.Name) + CountText(call.Arguments) + BlockOverhead;
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

    /** 内容块的字符规模,用于分块阈值与超限剥离的判定;图片按附件字节数计入。 */
    public static long EstimateContentChars(IReadOnlyList<ContentBlock> blocks)
    {
        var chars = 0L;
        foreach (var block in blocks)
        {
            chars += block switch
            {
                TextBlock text => text.Text.Length,
                ReasoningBlock reasoning => reasoning.Text.Length,
                ToolCallBlock call => (long)call.Name.Length + call.Arguments.Length,
                ToolResultBlock result => EstimateContentChars(result.Content),
                ImageBlock image => image.Attachment.Bytes,
                _ => 0,
            };
        }
        return chars;
    }

    public static long EstimateMessageChars(Message message) => EstimateContentChars(message.Content);

    public static int EstimateToolsTokens(EpochHeader? header)
        => header?.Tools is not { Count: > 0 } tools
            ? 0
            : CountText(DshJson.Serialize(tools)) + BlockOverhead;

    public static int EstimateHeader(EpochHeader? header)
        => EstimateToolsTokens(header);
}
