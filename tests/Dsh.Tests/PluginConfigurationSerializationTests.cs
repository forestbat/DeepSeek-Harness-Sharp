using System.Text.Json;
using Dsh.Boot;

namespace Dsh.Tests;

public sealed class PluginConfigurationSerializationTests
{
    [Fact]
    public void RawConfiguration_RoundTripsControlCharactersUnicodeAndNestedValues()
    {
        const string text = "quote\" slash\\ newline\n tab\t carriage\r backspace\b formfeed\f control\u0001 中文";
        var values = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["enabled"] = true,
            ["count"] = long.MaxValue,
            ["ratio"] = 1.25d,
            ["missing"] = null,
            ["nested"] = new Dictionary<string, object?> { ["key"] = text },
            ["list"] = new List<object?> { false, 7L, null, new int[] { 1, 2, 3 } },
        };
        var root = JsonSerializer.SerializeToElement(values, PluginConfigurationJsonContext.Default.Object);
        Assert.Equal(text, root.GetProperty("text").GetString());
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(long.MaxValue, root.GetProperty("count").GetInt64());
        Assert.Equal(1.25d, root.GetProperty("ratio").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("missing").ValueKind);
        Assert.Equal(text, root.GetProperty("nested").GetProperty("key").GetString());
        Assert.False(root.GetProperty("list")[0].GetBoolean());
        Assert.Equal(7L, root.GetProperty("list")[1].GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("list")[2].ValueKind);
        Assert.Equal(3, root.GetProperty("list")[3][2].GetInt32());
    }


    [Fact]
    public void JsonConfiguration_StaysStructuredAndUnsupportedValuesFailExplicitly()
    {
        using var source = JsonDocument.Parse("""{"configured":[1,true,null]}""");
        var root = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["json"] = source.RootElement,
            ["unsigned"] = ulong.MaxValue,
            ["decimal"] = 12.5m,
        }, PluginConfigurationJsonContext.Default.Object);
        Assert.True(root.GetProperty("json").GetProperty("configured")[1].GetBoolean());
        Assert.Equal(ulong.MaxValue, root.GetProperty("unsigned").GetUInt64());
        Assert.Equal(12.5m, root.GetProperty("decimal").GetDecimal());
        Assert.Throws<ArgumentException>(() => JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { ["invalid"] = double.NaN }, PluginConfigurationJsonContext.Default.Object));
    }
}
