using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Dsh.Toon;

/**
 * TOON (Token-Oriented Object Notation) 与 JSON 的互转,覆盖完整 JSON 值域,同一输入输出确定。
 * 形态: 对象逐行 key: value,子级缩进 2 空格;原始值数组 key[N]: a,b;一致键对象数组走表格式 key[N]{f1,f2}: 加逐行记录;
 * 其余数组走 key[N]: 加 "- " 列表项;空对象写作 {},空数组写作 [0]:。
 */
public static class ToonCodec
{
    private const int IndentWidth = 2;

    public static string Encode(JsonNode? value)
    {
        var lines = new List<string>();
        EncodeValue(value, 0, lines);
        return string.Join('\n', lines);
    }

    public static JsonNode? Decode(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select((raw, index) => new Line(raw, index + 1))
            .Where(line => line.Text.TrimEnd().Length > 0)
            .Select(line => line with { Text = line.Text.TrimEnd() })
            .ToList();
        if (lines.Count == 0)
            throw new FormatException("toon: empty document");
        var (value, next) = ParseBlock(lines, 0, IndentOf(lines[0]));
        if (next != lines.Count)
            throw new FormatException($"toon: trailing content at line {lines[next].Number}");
        return value;
    }

    private sealed record Line(string Text, int Number);

    private static int IndentOf(Line line)
    {
        var indent = 0;
        while (indent < line.Text.Length && line.Text[indent] == ' ')
            indent++;
        return indent;
    }

    private static void EncodeValue(JsonNode? value, int indent, List<string> lines)
    {
        switch (value)
        {
            case null:
                lines.Add(new string(' ', indent) + "null");
                return;
            case JsonValue scalar:
                lines.Add(new string(' ', indent) + ScalarText(scalar));
                return;
            case JsonObject obj:
                if (obj.Count == 0)
                {
                    lines.Add(new string(' ', indent) + "{}");
                    return;
                }
                foreach (var (key, member) in obj)
                    EncodeMember(key, member, indent, lines);
                return;
            case JsonArray array:
                EncodeArray(null, array, indent, lines);
                return;
        }
    }

    private static void EncodeMember(string key, JsonNode? member, int indent, List<string> lines)
    {
        var prefix = new string(' ', indent) + KeyText(key);
        switch (member)
        {
            case null:
            case JsonValue:
                lines.Add($"{prefix}: {ScalarText(member as JsonValue)}");
                return;
            case JsonObject obj when obj.Count == 0:
                lines.Add($"{prefix}: {{}}");
                return;
            case JsonObject obj:
                lines.Add($"{prefix}:");
                EncodeValue(obj, indent + IndentWidth, lines);
                return;
            case JsonArray array:
                EncodeArray(prefix, array, indent, lines);
                return;
        }
    }

    private static void EncodeArray(string? prefix, JsonArray array, int indent, List<string> lines)
    {
        var head = $"{prefix ?? new string(' ', indent)}[{array.Count}]";
        if (array.Count == 0)
        {
            lines.Add($"{head}:");
            return;
        }
        if (array.All(item => item is null or JsonValue))
        {
            lines.Add($"{head}: {string.Join(',', array.Select(item => ScalarText(item as JsonValue)))}");
            return;
        }
        if (TabularFields(array) is { } fields)
        {
            lines.Add($"{head}{{{string.Join(',', fields.Select(KeyText))}}}:");
            foreach (var item in array)
            {
                var row = (JsonObject)item!;
                lines.Add(new string(' ', indent + IndentWidth)
                    + string.Join(',', fields.Select(field => ScalarText(row[field] as JsonValue))));
            }
            return;
        }
        lines.Add($"{head}:");
        foreach (var item in array)
        {
            var nested = new List<string>();
            EncodeValue(item, indent + 2 * IndentWidth, nested);
            nested[0] = $"{new string(' ', indent + IndentWidth)}- {nested[0].TrimStart()}";
            lines.AddRange(nested);
        }
    }

    /** 一致键且字段值全为标量的对象数组走表格式,返回字段序;否则返回 null。 */
    private static string[]? TabularFields(JsonArray array)
    {
        if (array[0] is not JsonObject first || first.Count == 0)
            return null;
        var fields = first.Select(member => member.Key).ToArray();
        if (first.Any(member => member.Value is not null and not JsonValue))
            return null;
        foreach (var item in array)
        {
            if (item is not JsonObject obj || obj.Count != fields.Length)
                return null;
            if (!fields.SequenceEqual(obj.Select(member => member.Key)))
                return null;
            if (obj.Any(member => member.Value is not null and not JsonValue))
                return null;
        }
        return fields;
    }

    private static string ScalarText(JsonValue? scalar)
    {
        if (scalar is null)
            return "null";
        if (scalar.TryGetValue<string>(out var text))
            return QuoteIfNeeded(text);
        if (scalar.TryGetValue<bool>(out var flag))
            return flag ? "true" : "false";
        return scalar.ToJsonString();
    }

    private static string KeyText(string key)
        => key.Length > 0 && key.All(character => char.IsLetterOrDigit(character) || character is '_' or '.' or '-')
            ? key
            : Quote(key);

    private static string QuoteIfNeeded(string text)
    {
        var bare = text.Length > 0
            && text == text.Trim()
            && !text.Contains(',')
            && !text.Contains(':')
            && !text.Contains('"')
            && !text.Contains('\n')
            && text is not "true" and not "false" and not "null" and not "{}"
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        return bare ? text : Quote(text);
    }

    private static string Quote(string text)
        => $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")}\"";

    private static (JsonNode? Value, int Next) ParseBlock(IReadOnlyList<Line> lines, int start, int indent)
    {
        var first = lines[start];
        if (first.Text[indent..] == "{}")
            return (new JsonObject(), start + 1);
        if (first.Text[indent] == '-')
            return ParseList(lines, start, indent);
        if (TrySplitArrayHeader(first.Text[indent..], first.Number, out var rootCount, out var rootFields, out var rootInline))
            return ParseArrayBody(lines, start + 1, indent + IndentWidth, rootCount, rootFields, rootInline, first.Number);
        if (TrySplitKey(first.Text[indent..], first.Number, out _, out _, out _, out _))
            return ParseObject(lines, start, indent);
        if (lines.Count > start + 1 && IndentOf(lines[start + 1]) == indent)
            throw new FormatException($"toon: ambiguous bare line at line {lines[start + 1].Number}");
        return (ParseScalar(first.Text[indent..], first.Number), start + 1);
    }

    private static (JsonObject Value, int Next) ParseObject(IReadOnlyList<Line> lines, int start, int indent)
    {
        var result = new JsonObject();
        var cursor = start;
        while (cursor < lines.Count && IndentOf(lines[cursor]) == indent && lines[cursor].Text[indent] != '-')
        {
            var line = lines[cursor];
            var content = line.Text[indent..];
            if (!TrySplitKey(content, line.Number, out var key, out var arrayCount, out var fields, out var rest))
                break;
            cursor++;
            if (arrayCount is { } count)
            {
                var (array, next) = ParseArrayBody(lines, cursor, indent + IndentWidth, count, fields, rest, line.Number);
                result[key] = array;
                cursor = next;
                continue;
            }
            if (rest is { } scalar)
            {
                result[key] = scalar is "{}" ? new JsonObject() : ParseScalar(scalar, line.Number);
                continue;
            }
            if (cursor < lines.Count && IndentOf(lines[cursor]) > indent)
            {
                var (nested, next) = ParseBlock(lines, cursor, IndentOf(lines[cursor]));
                result[key] = nested;
                cursor = next;
            }
            else
            {
                result[key] = new JsonObject();
            }
        }
        return (result, cursor);
    }

    private static (JsonArray Value, int Next) ParseList(IReadOnlyList<Line> lines, int start, int indent)
    {
        var result = new JsonArray();
        var cursor = start;
        while (cursor < lines.Count && IndentOf(lines[cursor]) == indent && lines[cursor].Text[indent] == '-')
        {
            var line = lines[cursor];
            var payload = line.Text[(indent + 1)..].TrimStart();
            if (payload.Length == 0)
            {
                if (cursor + 1 >= lines.Count || IndentOf(lines[cursor + 1]) <= indent)
                    throw new FormatException($"toon: empty list item at line {line.Number}");
                var (nested, nextFromEmpty) = ParseBlock(lines, cursor + 1, IndentOf(lines[cursor + 1]));
                result.Add(nested);
                cursor = nextFromEmpty;
                continue;
            }
            var firstIndent = indent + IndentWidth;
            var shifted = new List<Line> { line with { Text = new string(' ', firstIndent) + payload } };
            var nextLine = cursor + 1;
            while (nextLine < lines.Count && IndentOf(lines[nextLine]) > indent)
            {
                shifted.Add(lines[nextLine] with { Text = lines[nextLine].Text[IndentWidth..] });
                nextLine++;
            }
            var (item, consumed) = ParseBlock(shifted, 0, firstIndent);
            if (consumed != shifted.Count)
                throw new FormatException($"toon: list item at line {line.Number} has inconsistent indentation");
            result.Add(item);
            cursor = nextLine;
        }
        return (result, cursor);
    }

    private static (JsonArray Value, int Next) ParseArrayBody(
        IReadOnlyList<Line> lines, int start, int indent, int count, string[]? fields, string? inline, int headerNumber)
    {
        if (inline is not null)
        {
            var result = new JsonArray();
            foreach (var cell in SplitInline(inline, headerNumber))
                result.Add(ParseScalar(cell, headerNumber));
            if (result.Count != count)
                throw new FormatException($"toon: array declares [{count}] but has {result.Count} items (line {headerNumber})");
            return (result, start);
        }
        if (fields is null)
        {
            var (items, nextAll) = ParseList(lines, start, indent);
            if (items.Count != count)
                throw new FormatException($"toon: array declares [{count}] but has {items.Count} items (line {headerNumber})");
            return (items, nextAll);
        }
        var records = new JsonArray();
        var cursor = start;
        for (var row = 0; row < count; row++)
        {
            if (cursor >= lines.Count || IndentOf(lines[cursor]) != indent)
                throw new FormatException($"toon: array declares [{count}] but fewer rows present (header line {headerNumber})");
            var line = lines[cursor];
            var cells = SplitInline(line.Text[indent..], line.Number);
            if (cells.Count != fields.Length)
                throw new FormatException($"toon: row at line {line.Number} has {cells.Count} cells, expected {fields.Length}");
            var record = new JsonObject();
            for (var column = 0; column < fields.Length; column++)
                record[fields[column]] = ParseScalar(cells[column], line.Number);
            records.Add(record);
            cursor++;
        }
        return (records, cursor);
    }

    /** 拆 `key`/`key[N]`/`key[N]{f1,f2}` 头与行内余量;非键行返回 false。 */
    private static bool TrySplitKey(
        string content, int lineNumber,
        out string key, out int? arrayCount, out string[]? fields, out string? rest)
    {
        key = "";
        arrayCount = null;
        fields = null;
        rest = null;
        var cursor = 0;
        if (content.StartsWith('"'))
            key = ParseQuoted(content, ref cursor, lineNumber);
        else
        {
            while (cursor < content.Length && content[cursor] != ':' && content[cursor] != '[')
                cursor++;
            key = content[..cursor];
            if (key.Length == 0)
                return false;
        }
        if (cursor < content.Length && content[cursor] == '[')
        {
            var close = content.IndexOf(']', cursor);
            if (close < 0)
                throw new FormatException($"toon: unclosed '[' at line {lineNumber}");
            if (!int.TryParse(content.AsSpan(cursor + 1, close - cursor - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var declared))
                throw new FormatException($"toon: bad array count at line {lineNumber}");
            arrayCount = declared;
            cursor = close + 1;
            if (cursor < content.Length && content[cursor] == '{')
            {
                var fieldsClose = content.IndexOf('}', cursor);
                if (fieldsClose < 0)
                    throw new FormatException($"toon: unclosed '{{' at line {lineNumber}");
                fields = content[(cursor + 1)..fieldsClose].Split(',');
                cursor = fieldsClose + 1;
            }
        }
        if (cursor >= content.Length)
            return false;
        if (content[cursor] != ':')
            return false;
        rest = content[(cursor + 1)..].TrimStart();
        if (rest.Length == 0)
            rest = null;
        return true;
    }

    private static bool TrySplitArrayHeader(string content, int lineNumber, out int count, out string[]? fields, out string? inline)
    {
        count = 0;
        fields = null;
        inline = null;
        if (!content.StartsWith('['))
            return false;
        if (!TrySplitKey("root" + content, lineNumber, out _, out var declared, out fields, out var rest))
            return false;
        if (declared is not { } value)
            return false;
        count = value;
        inline = rest;
        return true;
    }

    private static List<string> SplitInline(string text, int lineNumber)
    {
        var cells = new List<string>();
        var cursor = 0;
        while (cursor <= text.Length)
        {
            var comma = text.Length;
            var quoted = cursor < text.Length && text[cursor] == '"';
            if (quoted)
            {
                var scan = cursor + 1;
                while (scan < text.Length && (text[scan] != '"' || text[scan - 1] == '\\'))
                    scan++;
                comma = scan + 1;
            }
            else
            {
                comma = text.IndexOf(',', cursor);
                if (comma < 0)
                    comma = text.Length;
            }
            cells.Add(text[cursor..comma].Trim());
            cursor = comma + 1;
            if (cursor >= text.Length)
                break;
        }
        return cells;
    }

    private static JsonNode? ParseScalar(string text, int lineNumber)
    {
        if (text.StartsWith('"'))
        {
            var cursor = 0;
            var unquoted = ParseQuoted(text, ref cursor, lineNumber);
            if (cursor != text.Length)
                throw new FormatException($"toon: trailing content after quoted string at line {lineNumber}");
            return JsonValue.Create(unquoted);
        }
        return text switch
        {
            "null" => null,
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            _ => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                ? JsonValue.Create(integer)
                : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : JsonValue.Create(text),
        };
    }

    private static string ParseQuoted(string text, ref int cursor, int lineNumber)
    {
        if (cursor >= text.Length || text[cursor] != '"')
            throw new FormatException($"toon: expected quoted string at line {lineNumber}");
        var builder = new StringBuilder();
        cursor++;
        while (cursor < text.Length && text[cursor] != '"')
        {
            if (text[cursor] == '\\')
            {
                cursor++;
                if (cursor >= text.Length)
                    break;
                builder.Append(text[cursor] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => throw new FormatException($"toon: bad escape at line {lineNumber}"),
                });
                cursor++;
                continue;
            }
            builder.Append(text[cursor]);
            cursor++;
        }
        if (cursor >= text.Length)
            throw new FormatException($"toon: unterminated quote at line {lineNumber}");
        cursor++;
        return builder.ToString();
    }
}
