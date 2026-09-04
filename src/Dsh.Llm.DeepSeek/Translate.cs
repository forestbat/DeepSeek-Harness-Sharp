using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Dsh.Llm.DeepSeek;

public static class WireTranslate
{
    private sealed class OpenBlock
    {
        public required int Index;
        public required string Kind;
        public string Text = "";
        public string? CallId;
        public string? Name;
    }

    public static FinishReason MapFinishReason(string reason) => reason switch
    {
        "stop" => new FinishReason.Stop(),
        "tool_calls" => new FinishReason.ToolCalls(),
        "length" => new FinishReason.MaxTokens(),
        _ => new FinishReason.Error(new LlmFailure($"model stopped: {reason}", reason.ToUpperInvariant())),
    };

    public static TokenUsage MapUsage(WireUsage usage)
    {
        const double maxSafeInteger = 9007199254740991d;
        static bool IsSafeInteger(double value) => double.IsFinite(value) && Math.Floor(value) == value && Math.Abs(value) <= maxSafeInteger;
        var cacheRead = usage.PromptTokensDetails?.CachedTokens ?? usage.PromptCacheHitTokens;
        var reasoning = usage.CompletionTokensDetails?.ReasoningTokens;
        var combined = usage.PromptTokens + usage.CompletionTokens;
        var hasExactTotal = IsSafeInteger(usage.PromptTokens)
            && usage.PromptTokens >= 0
            && IsSafeInteger(usage.CompletionTokens)
            && usage.CompletionTokens >= 0
            && IsSafeInteger(combined)
            && (usage.TotalTokens is null || usage.TotalTokens == combined);
        return new TokenUsage(
            usage.PromptTokens - (cacheRead ?? 0),
            usage.CompletionTokens,
            hasExactTotal ? combined : null,
            cacheRead,
            null,
            reasoning);
    }

    public static async IAsyncEnumerable<StreamChunk> Translate(
        IAsyncEnumerable<string> payloads,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var nextIndex = 0;
        OpenBlock? textBlock = null;
        OpenBlock? reasoningBlock = null;
        var toolBlocks = new Dictionary<int, OpenBlock>();
        var order = new List<OpenBlock>();
        FinishReason? pendingFinish = null;
        TokenUsage? pendingUsage = null;

        OpenBlock Open(string kind)
        {
            var block = new OpenBlock { Index = nextIndex++, Kind = kind };
            order.Add(block);
            return block;
        }

        await foreach (var payload in payloads.WithCancellation(cancellationToken))
        {
            if (payload == SseParser.Done)
            {
                foreach (var block in order)
                    yield return new StreamChunk.BlockEnd(block.Index, CloseBlock(block));
                if (pendingUsage is not null)
                    yield return new StreamChunk.Usage(pendingUsage);
                var reason = pendingFinish ?? new FinishReason.Stop();
                if (reason is FinishReason.Stop && order.Count == 0)
                {
                    reason = new FinishReason.Error(new LlmFailure(
                        "model returned a completed response with no content",
                        LlmFailureCodes.EmptyResponse));
                }
                yield return new StreamChunk.Finish(reason);
                yield break;
            }

            WireChunk chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<WireChunk>(payload, DeepSeekJson.Options)
                    ?? new WireChunk();
            }
            catch (JsonException)
            {
                throw new LlmException(new LlmFailure(
                    $"malformed SSE payload: {payload[..Math.Min(payload.Length, 120)]}",
                    "MALFORMED_RESPONSE"));
            }

            foreach (var choice in chunk.Choices ?? [])
            {
                var delta = choice.Delta;

                if (delta?.ReasoningContent is { Length: > 0 } reasoning)
                {
                    if (reasoningBlock is null)
                    {
                        reasoningBlock = Open("reasoning");
                        yield return new StreamChunk.BlockStart(reasoningBlock.Index, "reasoning");
                    }
                    reasoningBlock.Text += reasoning;
                    yield return new StreamChunk.ReasoningDelta(reasoningBlock.Index, reasoning);
                }

                if (delta?.Content is { Length: > 0 } content)
                {
                    if (textBlock is null)
                    {
                        textBlock = Open("text");
                        yield return new StreamChunk.BlockStart(textBlock.Index, "text");
                    }
                    textBlock.Text += content;
                    yield return new StreamChunk.TextDelta(textBlock.Index, content);
                }

                foreach (var call in delta?.ToolCalls ?? [])
                {
                    if (!toolBlocks.TryGetValue(call.Index, out var block))
                    {
                        block = Open("tool-call");
                        toolBlocks[call.Index] = block;
                        yield return new StreamChunk.BlockStart(block.Index, "tool-call");
                    }
                    block.CallId = AcceptIdentity(block.CallId, call.Id);
                    block.Name = AcceptIdentity(block.Name, call.Function?.Name);
                    var fragment = call.Function?.Arguments ?? "";
                    block.Text += fragment;
                    yield return new StreamChunk.ToolCallDelta(
                        block.Index,
                        ToolCallId.Create(block.CallId ?? ""),
                        block.Name,
                        fragment);
                }

                if (choice.FinishReason is { } finishReason)
                    pendingFinish = MapFinishReason(finishReason);
            }

            if (chunk.Usage is { } usage)
                pendingUsage = MapUsage(usage);
        }

        throw new LlmException(new LlmFailure("SSE payload stream ended without [DONE]", LlmFailureCodes.StreamClosed));
    }

    private static string? AcceptIdentity(string? current, string? incoming)
        => !string.IsNullOrEmpty(incoming) ? incoming : current;

    private static ContentBlock CloseBlock(OpenBlock block) => block.Kind switch
    {
        "text" => new TextBlock(block.Text),
        "reasoning" => new ReasoningBlock(block.Text),
        "tool-call" => new ToolCallBlock(ToolCallId.Create(block.CallId ?? ""), block.Name ?? "", block.Text),
        _ => throw new InvalidOperationException($"cannot close block of kind \"{block.Kind}\""),
    };
}
