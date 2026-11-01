using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using Dsh.Plugins;

namespace Dsh.Tests;

public sealed class PluginConfigurationJsonTests
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
            ["nested"] = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?> { ["key"] = text }),
            ["list"] = new object?[] { false, 7L, null, new int[] { 1, 2, 3 } },
        };
        using var document = JsonDocument.Parse(PluginConfigurationJson.Write(values));
        var root = document.RootElement;
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
    public void NullAndEmptyMappings_KeepTheAbsentConfigurationContract()
    {
        Assert.Equal("", PluginConfigurationJson.Write(null));
        Assert.Equal("", PluginConfigurationJson.Write(new Dictionary<string, object?>()));
        Assert.Equal("", PluginConfigurationJson.Write(new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>())));
        Assert.Equal("", PluginConfigurationJson.Write(new Hashtable()));
        Assert.Equal("[]", PluginConfigurationJson.Write(Array.Empty<object?>()));
    }

    [Fact]
    public void JsonConfiguration_StaysStructuredAndUnsupportedValuesFailExplicitly()
    {
        using var source = JsonDocument.Parse("""{"configured":[1,true,null]}""");
        using var result = JsonDocument.Parse(PluginConfigurationJson.Write(new Dictionary<string, object?>
        {
            ["json"] = source.RootElement,
            ["unsigned"] = ulong.MaxValue,
            ["decimal"] = 12.5m,
        }));
        Assert.True(result.RootElement.GetProperty("json").GetProperty("configured")[1].GetBoolean());
        Assert.Equal(ulong.MaxValue, result.RootElement.GetProperty("unsigned").GetUInt64());
        Assert.Equal(12.5m, result.RootElement.GetProperty("decimal").GetDecimal());
        Assert.Throws<NotSupportedException>(() => PluginConfigurationJson.Write(new object()));
        Assert.Throws<NotSupportedException>(() => PluginConfigurationJson.Write(new Hashtable { [1] = "not a string key" }));
        Assert.Throws<ArgumentException>(() => PluginConfigurationJson.Write(new Dictionary<string, object?> { ["invalid"] = double.NaN }));
    }
}
