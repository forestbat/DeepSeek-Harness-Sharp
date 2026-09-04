using System.Text.Json;
using Dsh.Llm;
using Dsh.Llm.DeepSeek;

namespace Dsh.Tests;

public class TranslateTests
{
    private static async IAsyncEnumerable<string> Feed(params object[] payloads)
    {
        foreach (var payload in payloads)
            yield return payload is string text ? text : JsonSerializer.Serialize(payload);
    }

    private static async Task<List<StreamChunk>> Collect(IAsyncEnumerable<StreamChunk> stream)
    {
        var output = new List<StreamChunk>();
        await foreach (var chunk in stream)
            output.Add(chunk);
        return output;
    }

    private static readonly object FirstChunk = new
    {
        choices = new[] { new { delta = new { role = "assistant", content = (string?)null, reasoning_content = "" } } },
    };

    [Fact]
    public async Task Text_StreamsBlockAndDefersFinishToDone()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { content = "Hel" } } } },
            new { choices = new[] { new { delta = new { content = "lo" } } } },
            new { choices = new[] { new { delta = new { content = "" }, finish_reason = "stop" } }, usage = new { prompt_tokens = 5, completion_tokens = 2 } },
            SseParser.Done)));

        Assert.Equal(6, chunks.Count);
        Assert.Equal(new StreamChunk.BlockStart(0, "text"), chunks[0]);
        Assert.Equal(new StreamChunk.TextDelta(0, "Hel"), chunks[1]);
        Assert.Equal(new StreamChunk.TextDelta(0, "lo"), chunks[2]);
        Assert.Equal(new StreamChunk.BlockEnd(0, new TextBlock("Hello")), chunks[3]);
        Assert.Equal(new StreamChunk.Usage(new TokenUsage(5, 2, 7)), chunks[4]);
        Assert.Equal(new StreamChunk.Finish(new FinishReason.Stop()), chunks[5]);
    }

    [Fact]
    public async Task Text_AssemblesIntoMessage()
    {
        var assembler = new BlockAssembler();
        await foreach (var chunk in WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { content = "hi" } } } },
            new { choices = new[] { new { delta = new { }, finish_reason = "stop" } } },
            SseParser.Done)))
            assembler.Push(chunk);

        Assert.Equal([new TextBlock("hi")], assembler.Message().Content);
        Assert.Equal(new FinishReason.Stop(), assembler.Finish);
    }

    [Fact]
    public async Task Reasoning_EmptyFirstChunkDoesNotOpenBlock()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { content = "plain" } } } },
            new { choices = new[] { new { delta = new { }, finish_reason = "stop" } } },
            SseParser.Done)));

        Assert.DoesNotContain(chunks, chunk => chunk is StreamChunk.BlockStart { BlockType: "reasoning" });
    }

    [Fact]
    public async Task Reasoning_ThenTextAsSeparateBlocks()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { content = (string?)null, reasoning_content = "think" } } } },
            new { choices = new[] { new { delta = new { content = (string?)null, reasoning_content = "ing" } } } },
            new { choices = new[] { new { delta = new { content = "answer", reasoning_content = (string?)null } } } },
            new { choices = new[] { new { delta = new { }, finish_reason = "stop" } } },
            SseParser.Done)));

        Assert.Equal(8, chunks.Count);
        Assert.Equal(new StreamChunk.BlockStart(0, "reasoning"), chunks[0]);
        Assert.Equal(new StreamChunk.ReasoningDelta(0, "think"), chunks[1]);
        Assert.Equal(new StreamChunk.ReasoningDelta(0, "ing"), chunks[2]);
        Assert.Equal(new StreamChunk.BlockStart(1, "text"), chunks[3]);
        Assert.Equal(new StreamChunk.TextDelta(1, "answer"), chunks[4]);
        Assert.Equal(new StreamChunk.BlockEnd(0, new ReasoningBlock("thinking")), chunks[5]);
        Assert.Equal(new StreamChunk.BlockEnd(1, new TextBlock("answer")), chunks[6]);
        Assert.Equal(new StreamChunk.Finish(new FinishReason.Stop()), chunks[7]);
    }

    [Fact]
    public async Task ToolCall_ReassemblesFragmentedArguments()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "call_00_x", type = "function", @function = new { name = "get_weather", arguments = "" } } } } } } },
            new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, @function = new { arguments = "{\"city\"" } } } } } } },
            new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, @function = new { arguments = ": \"Paris\"}" } } } } } } },
            new { choices = new[] { new { delta = new { content = "" }, finish_reason = "tool_calls" } }, usage = new { prompt_tokens = 28, completion_tokens = 6 } },
            SseParser.Done)));

        Assert.Equal(7, chunks.Count);
        Assert.Equal(new StreamChunk.BlockStart(0, "tool-call"), chunks[0]);
        Assert.Equal(new StreamChunk.ToolCallDelta(0, ToolCallId.Create("call_00_x"), "get_weather", ""), chunks[1]);
        Assert.Equal(new StreamChunk.ToolCallDelta(0, ToolCallId.Create("call_00_x"), "get_weather", "{\"city\""), chunks[2]);
        Assert.Equal(new StreamChunk.ToolCallDelta(0, ToolCallId.Create("call_00_x"), "get_weather", ": \"Paris\"}"), chunks[3]);
        Assert.Equal(new StreamChunk.BlockEnd(0, new ToolCallBlock(ToolCallId.Create("call_00_x"), "get_weather", "{\"city\": \"Paris\"}")), chunks[4]);
        Assert.Equal(new StreamChunk.Usage(new TokenUsage(28, 6, 34)), chunks[5]);
        Assert.Equal(new StreamChunk.Finish(new FinishReason.ToolCalls()), chunks[6]);
    }

    [Fact]
    public async Task ToolCall_ParallelCallsDisambiguatedByWireIndex()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new { index = 0, id = "a", type = "function", @function = new { name = "one", arguments = "{}" } },
                                new { index = 1, id = "b", type = "function", @function = new { name = "two", arguments = "" } },
                            },
                        },
                    },
                },
            },
            new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 1, @function = new { arguments = "{}" } } } } } } },
            new { choices = new[] { new { delta = new { }, finish_reason = "tool_calls" } } },
            SseParser.Done)));

        var ends = chunks.OfType<StreamChunk.BlockEnd>().ToList();
        Assert.Equal(2, ends.Count);
        Assert.Equal(new StreamChunk.BlockEnd(0, new ToolCallBlock(ToolCallId.Create("a"), "one", "{}")), ends[0]);
        Assert.Equal(new StreamChunk.BlockEnd(1, new ToolCallBlock(ToolCallId.Create("b"), "two", "{}")), ends[1]);
    }

    [Fact]
    public async Task Usage_TrailingUsageOnlyChunk()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { content = "x" } } } },
            new { choices = new[] { new { delta = new { }, finish_reason = "stop" } }, usage = (object?)null },
            new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 9, completion_tokens = 1 } },
            SseParser.Done)));

        Assert.Equal(new StreamChunk.Usage(new TokenUsage(9, 1, 10)), chunks[^2]);
        Assert.Equal(new StreamChunk.Finish(new FinishReason.Stop()), chunks[^1]);
    }

    [Fact]
    public async Task Usage_LastUsageWins()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 1, completion_tokens = 1 } },
            new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 2, completion_tokens = 2 } },
            SseParser.Done)));

        var usage = chunks.OfType<StreamChunk.Usage>().Single();
        Assert.Equal(new TokenUsage(2, 2, 4), usage.Value);
    }

    [Fact]
    public async Task Finish_DefaultsToStopWithoutFinishReason()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { content = "x" } } } },
            SseParser.Done)));

        Assert.Equal(new StreamChunk.Finish(new FinishReason.Stop()), chunks[^1]);
    }

    [Fact]
    public async Task Finish_NoChoicesIsEmptyResponseError()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(new { }, SseParser.Done)));

        var finish = Assert.IsType<StreamChunk.Finish>(Assert.Single(chunks));
        var error = Assert.IsType<FinishReason.Error>(finish.Reason);
        Assert.Equal(LlmFailureCodes.EmptyResponse, error.Failure.Code);
    }

    [Fact]
    public async Task Finish_ExplicitStopWithNoBlocksIsEmptyResponseAfterUsage()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 7, completion_tokens = 0 } },
            SseParser.Done)));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new StreamChunk.Usage(new TokenUsage(7, 0, 7)), chunks[0]);
        var finish = Assert.IsType<StreamChunk.Finish>(chunks[1]);
        Assert.IsType<FinishReason.Error>(finish.Reason);
    }

    [Fact]
    public async Task Finish_ReasoningOnlyStreamStaysStop()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { content = (string?)null, reasoning_content = "mull" } } } },
            new { choices = new[] { new { delta = new { }, finish_reason = "stop" } } },
            SseParser.Done)));

        Assert.Equal(new StreamChunk.Finish(new FinishReason.Stop()), chunks[^1]);
    }

    [Fact]
    public async Task Finish_NonStopReasonWithoutBlocksStaysUnclassified()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { }, finish_reason = "length" } } },
            SseParser.Done)));

        Assert.Equal(new StreamChunk.Finish(new FinishReason.MaxTokens()), chunks[^1]);
    }

    [Fact]
    public async Task Error_MalformedJsonThrows()
    {
        await Assert.ThrowsAsync<LlmException>(async () => await Collect(WireTranslate.Translate(Feed("{bad json"))));
    }

    [Fact]
    public async Task Error_MissingDoneThrowsStreamClosed()
    {
        var error = await Assert.ThrowsAsync<LlmException>(async () => await Collect(WireTranslate.Translate(Feed(FirstChunk))));
        Assert.Contains("without [DONE]", error.Message);
    }

    [Theory]
    [InlineData("stop", "stop")]
    [InlineData("tool_calls", "tool-calls")]
    [InlineData("length", "max-tokens")]
    public void MapFinishReason_Known(string wire, string kind)
        => Assert.Equal(kind, WireTranslate.MapFinishReason(wire).Kind);

    [Theory]
    [InlineData("content_filter")]
    [InlineData("insufficient_system_resource")]
    [InlineData("mystery_reason")]
    public void MapFinishReason_UnknownBecomesError(string wire)
    {
        var error = Assert.IsType<FinishReason.Error>(WireTranslate.MapFinishReason(wire));
        Assert.Equal(wire.ToUpperInvariant(), error.Failure.Code);
        Assert.Equal($"model stopped: {wire}", error.Failure.Message);
    }

    [Fact]
    public void MapUsage_FullLiveCaptureShape()
    {
        var usage = WireTranslate.MapUsage(new WireUsage
        {
            PromptTokens = 283,
            CompletionTokens = 69,
            TotalTokens = 352,
            PromptCacheHitTokens = 256,
            PromptTokensDetails = new WirePromptTokensDetails { CachedTokens = 256 },
            CompletionTokensDetails = new WireCompletionTokensDetails { ReasoningTokens = 24 },
        });

        Assert.Equal(new TokenUsage(27, 69, 352, 256, null, 24), usage);
    }

    [Fact]
    public void MapUsage_FallsBackToCacheHitTokens()
    {
        var usage = WireTranslate.MapUsage(new WireUsage
        {
            PromptTokens = 10,
            CompletionTokens = 2,
            PromptCacheHitTokens = 8,
        });

        Assert.Equal(new TokenUsage(2, 2, 12, 8), usage);
    }

    [Fact]
    public void MapUsage_ReconstructsExactTotal()
    {
        var usage = WireTranslate.MapUsage(new WireUsage { PromptTokens = 10, CompletionTokens = 2 });
        Assert.Equal(new TokenUsage(10, 2, 12), usage);
    }

    [Theory]
    [InlineData(10, 2, 99.0)]
    [InlineData(-1, 2, null)]
    [InlineData(1.5, 2, null)]
    [InlineData(2, -1, null)]
    [InlineData(2, 1.5, null)]
    public void MapUsage_OmitsInexactTotal(double prompt, double completion, double? total)
    {
        var usage = WireTranslate.MapUsage(new WireUsage
        {
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = total,
        });

        Assert.Null(usage.TotalTokens);
        Assert.Equal(prompt, usage.InputTokens);
        Assert.Equal(completion, usage.OutputTokens);
    }

    [Fact]
    public async Task ToolCall_DeltaWithoutIdOrName()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, @function = new { arguments = "{}" } } } } } } },
            new { choices = new[] { new { delta = new { }, finish_reason = "tool_calls" } } },
            SseParser.Done)));

        Assert.Equal(4, chunks.Count);
        Assert.Equal(new StreamChunk.ToolCallDelta(0, ToolCallId.Create(""), null, "{}"), chunks[1]);
        Assert.Equal(new StreamChunk.BlockEnd(0, new ToolCallBlock(ToolCallId.Create(""), "", "{}")), chunks[2]);
    }

    [Fact]
    public async Task ToolCall_EmptyContinuationKeepsIdentity()
    {
        var chunks = await Collect(WireTranslate.Translate(Feed(
            FirstChunk,
            new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "call_00_x", type = "function", @function = new { name = "get_weather", arguments = "" } } } } } } },
            new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "", type = "function", @function = new { name = "", arguments = "{\"city\"" } } } } } } },
            new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "", type = "function", @function = new { name = "", arguments = ": \"Paris\"}" } } } } } } },
            new { choices = new[] { new { delta = new { }, finish_reason = "tool_calls" } } },
            SseParser.Done)));

        var end = chunks.OfType<StreamChunk.BlockEnd>().Single();
        Assert.Equal(new ToolCallBlock(ToolCallId.Create("call_00_x"), "get_weather", "{\"city\": \"Paris\"}"), end.Block);
    }
}
