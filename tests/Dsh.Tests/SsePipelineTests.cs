using System.Text;
using Dsh.Llm;
using Dsh.Llm.DeepSeek;

namespace Dsh.Tests;

public class SsePipelineTests
{
    [Fact]
    public async Task ToolCallStream_ProducesAllChunks()
    {
        var lines = new[]
        {
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"echo\",\"arguments\":\"{}\"}}]}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
            "",
            "data: [DONE]",
            "",
        };
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in WireTranslate.Translate(SseParser.Parse(stream)))
            chunks.Add(chunk);

        Assert.Equal(4, chunks.Count);
        Assert.IsType<StreamChunk.BlockStart>(chunks[0]);
        Assert.IsType<StreamChunk.ToolCallDelta>(chunks[1]);
        Assert.IsType<StreamChunk.BlockEnd>(chunks[2]);
        Assert.IsType<StreamChunk.Finish>(chunks[3]);
    }
}
