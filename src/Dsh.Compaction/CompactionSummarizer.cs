using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

/** 压缩摘要的分块与超限恢复:区间过大时切块并发摘要再递归合并;单次请求被 provider 以 payload 过大拒绝时剥离媒体与大块工具结果重试。 */
public static class CompactionSummarizer
{
    public const int TranscriptMaxChars = 16_000;
    public const int ChunkConcurrency = 3;
    public const int MaxDepth = 3;
    public const int ToolResultMaxChars = 2_000;

    public const string ChunkInstruction =
        """
        You are condensing ONE part of a longer conversation for a coding assistant. Summarize only this part: preserve exact file paths, commands, identifiers, numeric values, error strings, and decisions. Use terse bullets. Do NOT mention this summarization request or that the context was compacted.
        """;

    public const string MergeInstruction =
        """
        You are merging several partial summaries of one conversation into a single condensed checkpoint. Preserve exact file paths, commands, identifiers, numeric values, error strings, and decisions; drop duplication and stale details. Use terse bullets, then end with a single `TITLE: <concise conversation topic, at most 15 characters>` line.
        """;

    public static async Task<SummaryResult> Summarize(
        LlmRuntime llm,
        string provider,
        string model,
        int maxTokens,
        SummarizationInput input,
        IAgent agent,
        CancellationToken signal)
    {
        if (EstimatedChars(input) <= TranscriptMaxChars)
            return await SummarizeWithRecovery(llm, provider, model, maxTokens, input, agent, signal, allowChunkFallback: true);
        return await SummarizeChunked(llm, provider, model, maxTokens, input, agent, signal, allowChunkFallback: true);
    }

    private static long EstimatedChars(SummarizationInput input)
        => input.Messages.Sum(TokenEstimate.EstimateMessageChars);

    private static async Task<SummaryResult> SummarizeWithRecovery(
        LlmRuntime llm,
        string provider,
        string model,
        int maxTokens,
        SummarizationInput input,
        IAgent agent,
        CancellationToken signal,
        bool allowChunkFallback,
        string? instruction = null)
    {
        try
        {
            return await Summarizer.SummarizeWithLlm(llm, provider, model, maxTokens, input, agent, signal, instruction);
        }
        catch (Exception error) when (IsPayloadTooLarge(error))
        {
            var stripped = input with { Messages = StripForRecovery(input.Messages) };
            try
            {
                return await Summarizer.SummarizeWithLlm(llm, provider, model, maxTokens, stripped, agent, signal, instruction);
            }
            catch (Exception retryError) when (IsPayloadTooLarge(retryError))
            {
                // 单条消息本身超限时分块无法再拆分,再递归会无限下钻,直接沿用原始失败。
                if (!allowChunkFallback || Split(stripped.Messages, TranscriptMaxChars).Count <= 1)
                    throw;
                return await SummarizeChunked(llm, provider, model, maxTokens, stripped, agent, signal, allowChunkFallback: false);
            }
        }
    }

    private static async Task<SummaryResult> SummarizeChunked(
        LlmRuntime llm,
        string provider,
        string model,
        int maxTokens,
        SummarizationInput input,
        IAgent agent,
        CancellationToken signal,
        bool allowChunkFallback)
    {
        var chunks = Split(input.Messages, TranscriptMaxChars);
        var partials = new List<SummaryResult>(chunks.Count);
        for (var offset = 0; offset < chunks.Count; offset += ChunkConcurrency)
        {
            var batch = chunks.Skip(offset).Take(ChunkConcurrency).ToList();
            var results = await Task.WhenAll(batch.Select(messages =>
                SummarizeWithRecovery(llm, provider, model, maxTokens, input with { Messages = messages }, agent, signal, allowChunkFallback, ChunkInstruction)));
            partials.AddRange(results);
        }
        return await Merge(llm, provider, model, maxTokens, input, partials, agent, signal, allowChunkFallback, 0);
    }

    private static async Task<SummaryResult> Merge(
        LlmRuntime llm,
        string provider,
        string model,
        int maxTokens,
        SummarizationInput template,
        IReadOnlyList<SummaryResult> partials,
        IAgent agent,
        CancellationToken signal,
        bool allowChunkFallback,
        int depth)
    {
        var mergeMessages = partials
            .Select(result => MessageFactory.CreateUserText($"<partial-summary>\n{PlainText(result.Summary)}\n</partial-summary>"))
            .ToList();
        var mergeInput = template with { Messages = mergeMessages };
        if (EstimatedChars(mergeInput) <= TranscriptMaxChars || partials.Count <= 1 || depth >= MaxDepth)
            return await SummarizeWithRecovery(llm, provider, model, maxTokens, mergeInput, agent, signal, allowChunkFallback, MergeInstruction);
        var half = (partials.Count + 1) / 2;
        var left = await Merge(llm, provider, model, maxTokens, template, partials.Take(half).ToList(), agent, signal, allowChunkFallback, depth + 1);
        var right = await Merge(llm, provider, model, maxTokens, template, partials.Skip(half).ToList(), agent, signal, allowChunkFallback, depth + 1);
        return await Merge(llm, provider, model, maxTokens, template, [left, right], agent, signal, allowChunkFallback, depth + 2);
    }

    private static bool IsPayloadTooLarge(Exception error)
        => error is HarnessException && LlmFailureClassifiers.IsPayloadTooLargeError(LlmFailureClassifiers.ErrorChain(error));

    /** 贪心分块:每条消息不拆分,块大小不超过 maxChars(单条消息本身超限时独占一块)。 */
    public static List<IReadOnlyList<Message>> Split(IReadOnlyList<Message> messages, int maxChars)
    {
        var chunks = new List<IReadOnlyList<Message>>();
        var current = new List<Message>();
        var size = 0L;
        foreach (var message in messages)
        {
            var chars = TokenEstimate.EstimateMessageChars(message);
            if (current.Count > 0 && size + chars > maxChars)
            {
                chunks.Add(current);
                current = [];
                size = 0;
            }
            current.Add(message);
            size += chars;
        }
        if (current.Count > 0)
            chunks.Add(current);
        return chunks.Count > 0 ? chunks : [messages];
    }

    /** 剥离媒体附件并把大块工具结果截断,用于 payload 超限重试。 */
    public static IReadOnlyList<Message> StripForRecovery(IReadOnlyList<Message> messages)
        => messages.Select(message => message with { Content = StripBlocks(message.Content) }).ToList();

    private static IReadOnlyList<ContentBlock> StripBlocks(IReadOnlyList<ContentBlock> blocks)
        => blocks.Select(block => block switch
        {
            ImageBlock image => new TextBlock(
                $"[attached {image.Attachment.MediaType}: {image.Attachment.Name ?? image.Attachment.AttachmentId}] (media omitted for compaction)"),
            ToolResultBlock result => result with { Content = TruncateToolResult(result.Content) },
            _ => block,
        }).ToList();

    private static IReadOnlyList<ContentBlock> TruncateToolResult(IReadOnlyList<ContentBlock> content)
    {
        var text = MessageText.Flatten(content);
        if (text.Length <= ToolResultMaxChars)
            return content;
        return [new TextBlock($"{text[..ToolResultMaxChars]}\n[truncated for compaction]")];
    }

    private static string PlainText(IReadOnlyList<ContentBlock> summary)
        => string.Join('\n', summary.OfType<TextBlock>().Select(block => block.Text));
}
