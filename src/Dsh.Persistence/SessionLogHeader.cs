using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

internal sealed record SessionStorageMetadata(SessionHeader Meta, long InheritedEventCount);

internal static class SessionLogHeader
{
    public static void ValidateSeedCut(bool isSeeded, long? inheritedEventCount)
    {
        if (isSeeded && inheritedEventCount is null)
            throw new ArgumentException("seeded session header requires an inherited event count");
        if (!isSeeded && inheritedEventCount is not null and not 0)
            throw new ArgumentException("unseeded session header inherited event count must be 0");
    }

    public static string WriteHeaderLine(SessionHeader header, long? inheritedEventCount)
    {
        ValidateSeedCut(header.IsSeeded, inheritedEventCount);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "session");
            writer.WriteNumber("version", header.Version);
            writer.WriteString("id", header.Id.Value);
            writer.WriteNumber("createdAt", header.CreatedAt);
            if (header.Cwd is { } cwd) writer.WriteString("cwd", cwd);
            if (header.ParentSession is { } parent) writer.WriteString("parentSession", parent.Value);
            if (header.IsSeeded) writer.WriteNumber("seedLength", inheritedEventCount!.Value);
            if (header.Origin is { } origin) writer.WriteString("origin", origin);
            writer.WriteNumber("delegationDepth", header.DelegationDepth ?? 0);
            if (header.AgentPreset is { } agentPreset) writer.WriteString("agentPreset", agentPreset);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static SessionStorageMetadata? ParseHeader(string firstLine)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(firstLine); }
        catch (JsonException) { return null; }
        RefuseForeignFormatVersion(parsed);
        return FromHeaderLine(parsed);
    }

    public static SessionStorageMetadata ParseHeaderRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length == 0 || record[^1] != (byte)'\n' || record.IndexOf((byte)'\n') != record.Length - 1)
            throw new FormatException("empty or header-less session log");
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(Encoding.UTF8.GetString(record[..^1])); }
        catch (JsonException) { throw new FormatException("corrupt session log: header line is not valid JSON"); }
        RefuseForeignFormatVersion(parsed);
        return FromHeaderLine(parsed)
            ?? throw new FormatException("corrupt session log: first line is not a session header");
    }

    private static void RefuseForeignFormatVersion(JsonNode? parsed)
    {
        if (parsed is not JsonObject obj) return;
        if (obj["version"] is not JsonValue versionValue || !versionValue.TryGetValue<long>(out var version)) return;
        if (version == SessionHeader.SessionFormatVersion) return;
        var id = obj["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var idText) ? idText : "undefined";
        throw new SessionFormatUnsupportedException(SessionRefusals.FormatVersionRefusal(id, version));
    }

    private static SessionStorageMetadata? FromHeaderLine(JsonNode? parsed)
    {
        if (parsed is not JsonObject line) return null;
        if (line["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type) || type != "session")
            return null;
        if (!TryReadNonNegativeInt64(line["version"], out var version)) return null;
        if (line["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || id is null) return null;
        if (!TryReadNonNegativeInt64(line["createdAt"], out var createdAt)) return null;
        if (!TryReadNonNegativeInt64(line["delegationDepth"], out var delegationDepth)) return null;
        var seedLength = line["seedLength"];
        if (seedLength is not null && !TryReadNonNegativeInt64(seedLength, out _)) return null;
        if (line["origin"] is not null
            && (line["origin"] is not JsonValue originValue
                || !originValue.TryGetValue<string>(out var origin) || origin != "subagent"))
            return null;
        if (line["agentPreset"] is not null
            && (line["agentPreset"] is not JsonValue presetValue
                || !presetValue.TryGetValue<string>(out _)))
            return null;
        if (line["cwd"] is not null
            && (line["cwd"] is not JsonValue cwdValue || !cwdValue.TryGetValue<string>(out _)))
            return null;
        if (line["parentSession"] is not null
            && (line["parentSession"] is not JsonValue parentValue || !parentValue.TryGetValue<string>(out _)))
            return null;
        if (line.ContainsKey("sandboxMode") || line.ContainsKey("approvalPolicy"))
            throw new FormatException("session header uses retired policy baseline fields");
        var isSeeded = seedLength is not null;
        var meta = new SessionHeader
        {
            Version = checked((int)version),
            Id = SessionId.Create(id),
            CreatedAt = createdAt,
            Cwd = line["cwd"] is JsonValue cwdNode && cwdNode.TryGetValue<string>(out var cwd) ? cwd : null,
            ParentSession = line["parentSession"] is JsonValue parentNode && parentNode.TryGetValue<string>(out var parent)
                ? SessionId.Create(parent)
                : null,
            IsSeeded = isSeeded,
            Origin = line["origin"] is JsonValue originNode && originNode.TryGetValue<string>(out var originText) ? originText : null,
            DelegationDepth = checked((int)delegationDepth),
            AgentPreset = line["agentPreset"] is JsonValue presetNode && presetNode.TryGetValue<string>(out var preset) ? preset : null,
        };
        TryReadNonNegativeInt64(seedLength, out var cut);
        return new SessionStorageMetadata(meta, isSeeded ? cut : 0);
    }

    private static bool TryReadNonNegativeInt64(JsonNode? node, out long value)
    {
        value = 0;
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var number) && number >= 0)
        {
            value = number;
            return true;
        }
        return false;
    }
}
