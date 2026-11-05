using System.Text;
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
            : CountTextWithoutTokenizer(text);

    /**
     * 缺 o200k 数据包(Microsoft.ML.Tokenizers.Data.O200kBase)时的兜底估价: 按字符类别分开算, 而不是一律 chars/4。
     * CJK 在真实分词器里约 1 字 1 token, 用 chars/4 会把中文历史低估近 2 倍 —— 尾部保留预算与压缩日志的 token 数都会偏小。
     */
    private static int CountTextWithoutTokenizer(string text)
    {
        var total = 0;
        var cjk = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            total += 1;
            if (IsCjk(rune.Value))
                cjk += 1;
        }
        return cjk + (int)Math.Ceiling((total - cjk) / (double)FallbackCharsPerToken);
    }

    /** 统一表意文字、假名、谚文与全角字符: 与拉丁文相比, 一个字约等于一个 token。 */
    private static bool IsCjk(int codePoint)
        => codePoint is >= 0x2E80 and <= 0x9FFF
            or >= 0xAC00 and <= 0xD7AF
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFF00 and <= 0xFF65
            or >= 0x20000 and <= 0x3FFFF;

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
