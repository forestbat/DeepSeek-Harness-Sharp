using System.Text.Json.Nodes;
using Dsh.Toon;

namespace Dsh.Tests;

public sealed class ToonCodecTests
{
    private static JsonNode? RoundTrip(JsonNode? value) => ToonCodec.Decode(ToonCodec.Encode(value));

    [Fact]
    public void NestedObject_RoundTrips()
    {
        var value = JsonNode.Parse("""{"user":{"name":"Ada","address":{"city":"Paris","zip":"75001"}},"active":true}""");
        Assert.Equal(value!.ToJsonString(), RoundTrip(value)!.ToJsonString());
    }

    [Fact]
    public void PrimitiveArray_RoundTrips()
    {
        var value = JsonNode.Parse("""{"tags":["alpha","beta","gamma"]}""");
        Assert.Equal(value!.ToJsonString(), RoundTrip(value)!.ToJsonString());
        Assert.Contains("tags[3]: alpha,beta,gamma", ToonCodec.Encode(value));
    }

    [Fact]
    public void UniformObjectArray_UsesTabularForm()
    {
        var value = JsonNode.Parse("""{"users":[{"id":1,"name":"Ada"},{"id":2,"name":"Bob"}]}""");
        var encoded = ToonCodec.Encode(value);
        Assert.Contains("users[2]{id,name}:", encoded);
        Assert.Equal(value!.ToJsonString(), ToonCodec.Decode(encoded)!.ToJsonString());
    }

    [Fact]
    public void NonUniformArray_RoundTrips()
    {
        var value = JsonNode.Parse("""{"items":[{"a":1},42,"text",{"b":[1,2]}]}""");
        Assert.Equal(value!.ToJsonString(), RoundTrip(value)!.ToJsonString());
    }

    [Fact]
    public void RootArray_RoundTrips()
    {
        var value = JsonNode.Parse("""[1,2,3]""");
        Assert.Equal("[3]: 1,2,3", ToonCodec.Encode(value));
        Assert.Equal(value!.ToJsonString(), RoundTrip(value)!.ToJsonString());
    }

    [Fact]
    public void EmptyShapes_RoundTrip()
    {
        var value = JsonNode.Parse("""{"empty":{},"nothing":[],"rootNull":null}""");
        Assert.Equal(value!.ToJsonString(), RoundTrip(value)!.ToJsonString());
    }

    [Fact]
    public void EmptyObject_RoundTrips()
    {
        Assert.Equal("{}", ToonCodec.Encode(JsonNode.Parse("{}")));
        Assert.Equal("{}", RoundTrip(JsonNode.Parse("{}"))!.ToJsonString());
    }

    [Theory]
    [InlineData("has, comma")]
    [InlineData("has: colon")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData(" padded")]
    [InlineData("line\nbreak")]
    public void StringsNeedingQuotes_RoundTrip(string text)
    {
        var value = new JsonObject { ["text"] = text };
        Assert.Equal(value.ToJsonString(), RoundTrip(value)!.ToJsonString());
    }

    [Fact]
    public void UnicodeAndNumbers_RoundTrip()
    {
        var value = JsonNode.Parse("""{"name":"张三","big":1234567890123,"pi":3.14,"neg":-7,"flag":false}""");
        Assert.Equal(value!.ToJsonString(), RoundTrip(value)!.ToJsonString());
    }

    [Fact]
    public void Encode_IsDeterministicAndPreservesKeyOrder()
    {
        var value = JsonNode.Parse("""{"z":1,"a":{"y":2,"b":3}}""");
        var first = ToonCodec.Encode(value);
        Assert.Equal(first, ToonCodec.Encode(value));
        Assert.True(first.IndexOf("z:", StringComparison.Ordinal) < first.IndexOf("a:", StringComparison.Ordinal));
    }

    [Fact]
    public void Decode_ArrayCountMismatch_Throws()
        => Assert.Throws<FormatException>(() => ToonCodec.Decode("tags[3]: a,b"));

    [Fact]
    public void Decode_UnterminatedQuote_Throws()
        => Assert.Throws<FormatException>(() => ToonCodec.Decode("name: \"unclosed"));

    [Fact]
    public void Decode_ToleratesCrlfAndTrailingNewline()
    {
        var value = ToonCodec.Decode("a: 1\r\nb: 2\r\n");
        Assert.Equal(1L, value!["a"]!.GetValue<long>());
        Assert.Equal(2L, value!["b"]!.GetValue<long>());
    }
}
