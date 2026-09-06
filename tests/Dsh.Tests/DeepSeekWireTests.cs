using System.Text.Json;
using Dsh.Llm;
using Dsh.Llm.DeepSeek;

namespace Dsh.Tests;

public sealed class DeepSeekWireTests
{
    [Fact]
    public void SerializeRequestIncludesMessageContent()
    {
        var options = new GenerateOptions
        {
            Provider = "deepseek-official",
            Model = "deepseek-v4-flash",
            System = "system prompt",
            Messages = [MessageFactory.CreateUserText("hello")],
        };
        var wire = WireSerialize.SerializeRequest(options, new RequestDefaults());
        var json = JsonSerializer.Serialize(wire, DeepSeekJson.Options);
        using var document = JsonDocument.Parse(json);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system prompt", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("hello", messages[1].GetProperty("content").GetString());
    }
}
