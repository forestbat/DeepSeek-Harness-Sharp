using System.Text;
using Dsh.Llm;

namespace Dsh.Tests;

public sealed class StreamingResponseAutoMapperTests
{
    [Fact]
    public async Task MapsOpenAiCompatibleDeltaWithEmptyFinishReason()
    {
        var payload = """
            data: {"choices":[{"delta":{"role":"assistant","reasoning_content":"We need answer."},"finish_reason":""}]}

            data: {"choices":[{"delta":{"content":"Hello!"},"finish_reason":""}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}

            data: [DONE]

            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in StreamingResponseAutoMapper.Translate(stream, CancellationToken.None))
            chunks.Add(chunk);
        Assert.Contains(chunks, chunk => chunk is StreamChunk.TextDelta { Text: "Hello!" });
        var textBlocks = chunks.OfType<StreamChunk.BlockEnd>()
            .Select(block => block.Block)
            .OfType<TextBlock>()
            .ToList();
        Assert.Contains(textBlocks, block => block.Text == "Hello!");
    }
}