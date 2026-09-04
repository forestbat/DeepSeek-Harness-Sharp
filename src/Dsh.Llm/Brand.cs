using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm;

public interface IBrand<TSelf> where TSelf : struct, IBrand<TSelf>
{
    static abstract TSelf Create(string value);
    string Value { get; }
}

public sealed class BrandJsonConverter<T> : JsonConverter<T> where T : struct, IBrand<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => T.Create(reader.GetString() ?? throw new JsonException("brand id must be a string"));

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(BrandJsonConverter<MessageId>))]
public readonly record struct MessageId(string Value) : IBrand<MessageId>
{
    public static MessageId Create(string value) => new(value);
    public override string ToString() => Value;
}

[JsonConverter(typeof(BrandJsonConverter<ToolCallId>))]
public readonly record struct ToolCallId(string Value) : IBrand<ToolCallId>
{
    public static ToolCallId Create(string value) => new(value);
    public override string ToString() => Value;
}

[JsonConverter(typeof(BrandJsonConverter<ProviderRequestId>))]
public readonly record struct ProviderRequestId(string Value) : IBrand<ProviderRequestId>
{
    public static ProviderRequestId Create(string value) => new(value);
    public override string ToString() => Value;
}

[JsonConverter(typeof(BrandJsonConverter<ReasoningEffortId>))]
public readonly record struct ReasoningEffortId(string Value) : IBrand<ReasoningEffortId>
{
    public static ReasoningEffortId Create(string value) => new(value);
    public override string ToString() => Value;
}

[JsonConverter(typeof(BrandJsonConverter<SessionId>))]
public readonly record struct SessionId(string Value) : IBrand<SessionId>
{
    public static SessionId Create(string value) => new(value);
    public override string ToString() => Value;
}
