using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

/**
 * 压缩摘要的分块与超限恢复: 区间过大时切块并发摘要再递归合并; 单次请求被 provider 以 payload 过大拒绝时剥离媒体与大块工具结果重试。
 *
 * 分块预算按模型上下文推算, 不按固定字符数。这点决定了压缩要跑多久: 摘要请求本身要生成输出,
 * 输出生成才是耗时的主体, 因此"多少个块"几乎等于"多少倍耗时"。用固定 16k 字符(约 4k token)切块,
 * 一次几十 MB 的会话会被切成上百块, 每个请求都生成一段摘要 —— 十几分钟就是这么来的。
 * 参考实现(Kilo Code 的 compaction-chunks)同样把块预算算成 usable(context, output) * 0.6。
 */
public static class CompactionSummarizer
{
    /** 单个文本/推理片段进入摘要请求前的裁剪上限(Kilo 里同名常量是片段裁剪, 不是分块预算)。 */
    public const int PartMaxChars = 16_000;

    /** 单个工具结果进入摘要请求前的裁剪上限。 */
    public const int ToolResultMaxChars = 2_000;

    /** 分块预算占"可用上下文"的比例: 留出输出与系统提示的余量。 */
    public const double ChunkBudgetRatio = 0.6;

    /**
     * 分块决策的估算口径: 约 4 字符/token。
     * 这里刻意不用真实分词器 —— 预算本身留了 40% 余量, 而几 MB 历史跑一遍分词要几秒, 不值得。
     */
    public const int CharsPerToken = 4;

    /** 模型窗口未知时的分块预算: 不敢按大窗口切块, 保守换稳定。 */
    public const int UnknownContextChunkTokens = 8_000;

    public const int MinChunkTokens = 1_000;

    /** 服务端报 payload 过大时预算的收缩倍数: 一次收缩就把块降到它肯接收的量级。 */
    public const int PayloadShrinkFactor = 4;
    public const int ChunkConcurrency = 3;
    public const int MaxDepth = 3;

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
        // 先裁剪再判断是否需要分块: 会话里工具结果常占一半以上字符, 不裁剪会把块数放大一个数量级。
        // 参考实现(Kilo)在每次转换时就固定 stripMedia + 截断工具输出, 而不是只在超限恢复时才做。
        var slimmed = input with { Messages = Slim(input.Messages) };
        var budget = ChunkBudgetTokens(llm, provider, model, maxTokens);
        if (EstimatedTokens(slimmed.Messages) <= budget)
            return await SummarizeWithRecovery(llm, provider, model, maxTokens, slimmed, agent, signal, allowChunkFallback: true);
        return await SummarizeChunked(llm, provider, model, maxTokens, slimmed, agent, signal, allowChunkFallback: true, budget);
    }

    /**
     * 单次摘要请求能装多少历史: 按模型可用上下文算, 而不是固定字符数。
     * 同一份会话内容, 块越大请求越少, 压缩越快; 只要不超出模型窗口就不会被拒。
     */
    public static int ChunkBudgetTokens(LlmRuntime llm, string provider, string model, int maxTokens)
    {
        var contextWindow = ContextWindowOf(llm, provider, model);
        if (contextWindow is not { } context || context <= 0)
            return UnknownContextChunkTokens;
        var usable = context - maxTokens;
        return usable <= MinChunkTokens
            ? MinChunkTokens
            : Math.Max(MinChunkTokens, (int)Math.Floor(usable * ChunkBudgetRatio));
    }

    /**
     * 窗口信息只是"能切多大块"的优化参数: 取不到时按未知处理走保守预算。
     * 未注册的 provider 会让 ResolveModelInfo 抛异常, 那种情况下连摘要请求本身也会失败,
     * 但那时该由真正的请求错误说话, 不该在这里先抛出来。
     */
    private static int? ContextWindowOf(LlmRuntime llm, string provider, string model)
    {
        try
        {
            return llm.ResolveModelInfo(provider, model).ContextWindow;
        }
        catch (LlmException)
        {
            return null;
        }
    }

    public static int EstimatedTokens(IReadOnlyList<Message> messages)
        => (int)Math.Ceiling(messages.Sum(TokenEstimate.EstimateMessageChars) / (double)CharsPerToken);

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
        catch (SummaryUnusableException) when (!signal.IsCancellationRequested)
        {
            // 模型偶发返回空内容(小模型 / maxTokens 偏小最常见): 重试一次再判定失败, 不要一次空输出就整次放弃。
            return await Summarizer.SummarizeWithLlm(llm, provider, model, maxTokens, input, agent, signal, instruction);
        }
        catch (Exception error) when (IsPayloadTooLarge(error))
        {
            // 服务端认为太大, 说明预算比它实际吃下的更大(目录里的窗口可能偏乐观)。
            // 缩小预算重新切块才有意义: 内容已经裁剪过, 原样重发是白跑一趟。
            var budget = ChunkBudgetTokens(llm, provider, model, maxTokens);
            var smaller = Math.Max(MinChunkTokens, budget / PayloadShrinkFactor);
            if (!allowChunkFallback || smaller >= budget || Split(input.Messages, smaller).Count <= 1)
                throw;
            return await SummarizeChunked(llm, provider, model, maxTokens, input, agent, signal, allowChunkFallback: false, smaller);
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
        bool allowChunkFallback,
        int budgetTokens)
    {
        var chunks = Split(input.Messages, budgetTokens);
        var partials = new List<SummaryResult>(chunks.Count);
        for (var offset = 0; offset < chunks.Count; offset += ChunkConcurrency)
        {
            var batch = chunks.Skip(offset).Take(ChunkConcurrency).ToList();
            var results = await Task.WhenAll(batch.Select(messages =>
                SummarizeWithRecovery(llm, provider, model, maxTokens, input with { Messages = messages }, agent, signal, allowChunkFallback, ChunkInstruction)));
            partials.AddRange(results);
        }
        return await Merge(llm, provider, model, maxTokens, input, partials, agent, signal, allowChunkFallback, 0, budgetTokens);
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
        int depth,
        int budget)
    {
        var mergeMessages = partials
            .Select(result => MessageFactory.CreateUserText($"<partial-summary>\n{PlainText(result.Summary)}\n</partial-summary>"))
            .ToList();
        var mergeInput = template with { Messages = mergeMessages };
        if (EstimatedTokens(mergeMessages) <= budget || partials.Count <= 1 || depth >= MaxDepth)
            return await SummarizeWithRecovery(llm, provider, model, maxTokens, mergeInput, agent, signal, allowChunkFallback, MergeInstruction);
        var half = (partials.Count + 1) / 2;
        var left = await Merge(llm, provider, model, maxTokens, template, partials.Take(half).ToList(), agent, signal, allowChunkFallback, depth + 1, budget);
        var right = await Merge(llm, provider, model, maxTokens, template, partials.Skip(half).ToList(), agent, signal, allowChunkFallback, depth + 1, budget);
        return await Merge(llm, provider, model, maxTokens, template, [left, right], agent, signal, allowChunkFallback, depth + 2, budget);
    }

    private static bool IsPayloadTooLarge(Exception error)
        => error is HarnessException && LlmFailureClassifiers.IsPayloadTooLargeError(LlmFailureClassifiers.ErrorChain(error));

    /** 贪心分块: 每条消息不拆分, 块大小不超过 maxTokens(单条消息本身超限时独占一块)。 */
    public static List<IReadOnlyList<Message>> Split(IReadOnlyList<Message> messages, int maxTokens)
    {
        var chunks = new List<IReadOnlyList<Message>>();
        var current = new List<Message>();
        var size = 0;
        foreach (var message in messages)
        {
            var tokens = (int)Math.Ceiling(TokenEstimate.EstimateMessageChars(message) / (double)CharsPerToken);
            if (current.Count > 0 && size + tokens > maxTokens)
            {
                chunks.Add(current);
                current = [];
                size = 0;
            }
            current.Add(message);
            size += tokens;
        }
        if (current.Count > 0)
            chunks.Add(current);
        return chunks.Count > 0 ? chunks : [messages];
    }

    /**
     * 精简进入摘要请求的历史: 图片降级为占位文本、超长工具结果与超长文本片段截断。
     * 每次压缩都做, 不只是超限恢复 —— 这正是请求能被控制在一个很小的数里的原因。
     */
    public static IReadOnlyList<Message> Slim(IReadOnlyList<Message> messages)
        => messages.Select(message => message with { Content = SlimBlocks(message.Content) }).ToList();

    private static IReadOnlyList<ContentBlock> SlimBlocks(IReadOnlyList<ContentBlock> blocks)
        => blocks.Select(block => block switch
        {
            ImageBlock image => new TextBlock(
                $"[attached {image.Attachment.MediaType}: {image.Attachment.Name ?? image.Attachment.AttachmentId}] (media omitted for compaction)"),
            ToolResultBlock result => result with { Content = TruncateToolResult(result.Content) },
            // 单条超长文本(整份文件、超长命令输出)同样要收口, 否则它自己就撑爆一个块。
            TextBlock text => TruncateText(text),
            ReasoningBlock reasoning => TruncateReasoning(reasoning),
            _ => block,
        }).ToList();

    private static ContentBlock TruncateText(TextBlock block)
        => block.Text.Length <= PartMaxChars
            ? block
            : new TextBlock($"{block.Text[..PartMaxChars]}\n[truncated for compaction]");

    private static ContentBlock TruncateReasoning(ReasoningBlock block)
        => block.Text.Length <= PartMaxChars
            ? block
            : block with { Text = $"{block.Text[..PartMaxChars]}\n[truncated for compaction]" };

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
