using System.Text.Json;

namespace Dsh.Interaction;

/** 三层 diff 提取: ① resultMeta.card=="diff" 显式契约; ② unified/apply_patch 文本识别; ③ old/new 成对字段走行级 LCS。 */
public static class DiffCardExtractor
{
    private const int MaxLcsCells = 1_000_000;

    private static readonly (string Old, string New)[] PairNames =
    [
        ("old_string", "new_string"),
        ("old_str", "new_str"),
        ("oldText", "newText"),
        ("before", "after"),
        ("old", "new"),
    ];

    private static readonly string[] PathNames = ["file_path", "path", "filename"];

    public static DiffCard? TryExtract(string? toolName, string? argumentsJson, JsonElement? resultMeta, string? resultText = null)
        => FromMeta(resultMeta)
           ?? FromPatchTexts(argumentsJson, resultText)
           ?? FromPairs(toolName, argumentsJson, resultMeta);

    public static IReadOnlyList<DiffLine> DiffLines(string? oldText, string? newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length
               && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
            prefix++;
        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
               && string.Equals(oldLines[^(suffix + 1)], newLines[^(suffix + 1)], StringComparison.Ordinal))
            suffix++;
        var lines = new List<DiffLine>();
        for (var index = 0; index < prefix; index++)
            lines.Add(new DiffLine(DiffLineKind.Context, index + 1, index + 1, oldLines[index]));
        EmitMiddle(lines, oldLines, newLines, prefix, oldLines.Length - suffix, newLines.Length - suffix);
        for (var index = 0; index < suffix; index++)
        {
            var oldLine = oldLines.Length - suffix + index + 1;
            lines.Add(new DiffLine(DiffLineKind.Context, oldLine, newLines.Length - suffix + index + 1, oldLines[oldLine - 1]));
        }
        return lines;
    }

    private static void EmitMiddle(List<DiffLine> lines, string[] oldLines, string[] newLines, int start, int oldEnd, int newEnd)
    {
        var oldCount = oldEnd - start;
        var newCount = newEnd - start;
        var ops = oldCount * newCount > MaxLcsCells
            ? null
            : LcsAlignment(oldLines, newLines, start, oldEnd, newEnd);
        var oldNo = start + 1;
        var newNo = start + 1;
        if (ops is null)
        {
            for (var index = start; index < oldEnd; index++)
                lines.Add(new DiffLine(DiffLineKind.Delete, oldNo++, null, oldLines[index]));
            for (var index = start; index < newEnd; index++)
                lines.Add(new DiffLine(DiffLineKind.Add, null, newNo++, newLines[index]));
            return;
        }
        foreach (var op in ops)
        {
            if (op == DiffLineKind.Context)
                lines.Add(new DiffLine(DiffLineKind.Context, oldNo++, newNo++, oldLines[oldNo - 2]));
            else if (op == DiffLineKind.Delete)
                lines.Add(new DiffLine(DiffLineKind.Delete, oldNo++, null, oldLines[oldNo - 2]));
            else
                lines.Add(new DiffLine(DiffLineKind.Add, null, newNo++, newLines[newNo - 2]));
        }
    }

    /** 经典 DP LCS 回溯出 context/delete/add 操作序列; 仅供中小规模中段使用(上游已裁剪公共前后缀)。 */
    private static List<DiffLineKind> LcsAlignment(string[] oldLines, string[] newLines, int start, int oldEnd, int newEnd)
    {
        var oldCount = oldEnd - start;
        var newCount = newEnd - start;
        var lengths = new int[oldCount + 1, newCount + 1];
        for (var i = oldCount - 1; i >= 0; i--)
        for (var j = newCount - 1; j >= 0; j--)
            lengths[i, j] = string.Equals(oldLines[start + i], newLines[start + j], StringComparison.Ordinal)
                ? lengths[i + 1, j + 1] + 1
                : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var ops = new List<DiffLineKind>();
        var x = 0;
        var y = 0;
        while (x < oldCount && y < newCount)
        {
            if (string.Equals(oldLines[start + x], newLines[start + y], StringComparison.Ordinal))
            {
                ops.Add(DiffLineKind.Context);
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                ops.Add(DiffLineKind.Delete);
                x++;
            }
            else
            {
                ops.Add(DiffLineKind.Add);
                y++;
            }
        }
        while (x++ < oldCount) ops.Add(DiffLineKind.Delete);
        while (y++ < newCount) ops.Add(DiffLineKind.Add);
        return ops;
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static DiffCard? FromMeta(JsonElement? resultMeta)
    {
        if (resultMeta is not { ValueKind: JsonValueKind.Object } meta)
            return null;
        if (!meta.TryGetProperty("card", out var card) || card.ValueKind != JsonValueKind.String || card.GetString() != "diff")
            return null;
        if (!meta.TryGetProperty("diffs", out var diffs) || diffs.ValueKind != JsonValueKind.Array)
            return null;
        var title = meta.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString() ?? "diff"
            : "diff";
        var lines = new List<DiffLine>();
        var multiFile = diffs.GetArrayLength() > 1;
        foreach (var diff in diffs.EnumerateArray())
        {
            var path = ReadString(diff, "path");
            if (multiFile && path is not null)
                lines.Add(new DiffLine(DiffLineKind.Context, null, null, $"*** {path}"));
            lines.AddRange(DiffLines(ReadString(diff, "oldText"), ReadString(diff, "newText")));
        }
        return HasChanges(lines) ? new DiffCard(title, "meta", lines) : null;
    }

    private static DiffCard? FromPatchTexts(string? argumentsJson, string? resultText)
    {
        foreach (var candidate in CandidateTexts(argumentsJson, resultText))
        {
            if (TryParseUnifiedDiff(candidate) is { } unified)
                return unified;
            if (TryParseApplyPatch(candidate) is { } applied)
                return applied;
        }
        return null;
    }

    private static IEnumerable<string> CandidateTexts(string? argumentsJson, string? resultText)
    {
        if (argumentsJson is not null)
        {
            List<string> fields;
            try
            {
                using var document = JsonDocument.Parse(argumentsJson);
                fields = [];
                CollectStrings(document.RootElement, fields);
            }
            catch (JsonException)
            {
                fields = [];
            }
            foreach (var field in fields)
                yield return field;
        }
        if (resultText is not null)
            yield return resultText;
    }

    private static void CollectStrings(JsonElement element, List<string> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                output.Add(element.GetString() ?? "");
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    CollectStrings(property.Value, output);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectStrings(item, output);
                break;
        }
    }

    private static DiffCard? TryParseUnifiedDiff(string text)
    {
        if (!text.Contains("@@ -", StringComparison.Ordinal) || !text.Contains("+++ ", StringComparison.Ordinal))
            return null;
        var lines = new List<DiffLine>();
        string? title = null;
        var oldNo = 0;
        var newNo = 0;
        var inHunk = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("+++ ", StringComparison.Ordinal))
            {
                title ??= StripDiffPrefix(raw[4..].Trim());
                inHunk = false;
                continue;
            }
            if (raw.StartsWith("@@ -", StringComparison.Ordinal) && TryParseHunkHeader(raw, out var oldStart, out var newStart))
            {
                oldNo = oldStart;
                newNo = newStart;
                inHunk = true;
                continue;
            }
            if (!inHunk)
                continue;
            if (raw.StartsWith('\\'))
                continue;
            if (!AppendPatchLine(lines, raw, ref oldNo, ref newNo))
                inHunk = false;
        }
        return HasChanges(lines) ? new DiffCard(title ?? "patch", "patch", lines) : null;
    }

    private static bool TryParseHunkHeader(string line, out int oldStart, out int newStart)
    {
        oldStart = 0;
        newStart = 0;
        var plus = line.IndexOf('+', 4);
        var close = line.IndexOf(" @@", plus < 0 ? 4 : plus, StringComparison.Ordinal);
        if (plus < 0 || close < 0)
            return false;
        var oldSpan = line.AsSpan(4, plus - 5);
        var newSpan = line.AsSpan(plus + 1, close - plus - 1);
        return TryParseRangeStart(oldSpan, out oldStart) && TryParseRangeStart(newSpan, out newStart);
    }

    private static bool TryParseRangeStart(ReadOnlySpan<char> range, out int start)
    {
        var comma = range.IndexOf(',');
        return int.TryParse(comma < 0 ? range : range[..comma], out start);
    }

    private static string StripDiffPrefix(string path)
        => path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;

    private static DiffCard? TryParseApplyPatch(string text)
    {
        if (!text.Contains("*** Begin Patch", StringComparison.Ordinal))
            return null;
        var lines = new List<DiffLine>();
        string? title = null;
        var oldNo = 1;
        var newNo = 1;
        var inFile = false;
        var fileCount = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("*** End Patch", StringComparison.Ordinal))
                break;
            if (raw.StartsWith("*** ", StringComparison.Ordinal))
            {
                inFile = TryPatchFileMarker(raw, lines, ref title, ref fileCount);
                oldNo = 1;
                newNo = 1;
                continue;
            }
            if (!inFile || raw.StartsWith("@@", StringComparison.Ordinal))
                continue;
            AppendPatchLine(lines, raw, ref oldNo, ref newNo);
        }
        return HasChanges(lines) ? new DiffCard(title ?? "patch", "patch", lines) : null;
    }

    private static bool TryPatchFileMarker(string line, List<DiffLine> lines, ref string? title, ref int fileCount)
    {
        string? path = null;
        foreach (var marker in new[] { "*** Update File: ", "*** Add File: ", "*** Delete File: " })
        {
            if (line.StartsWith(marker, StringComparison.Ordinal))
                path = line[marker.Length..].Trim();
        }
        if (path is null)
            return false;
        title ??= path;
        fileCount++;
        if (fileCount > 1)
            lines.Add(new DiffLine(DiffLineKind.Context, null, null, $"*** {path}"));
        return true;
    }

    private static bool AppendPatchLine(List<DiffLine> lines, string raw, ref int oldNo, ref int newNo)
    {
        if (raw.StartsWith('+'))
            lines.Add(new DiffLine(DiffLineKind.Add, null, newNo++, raw[1..]));
        else if (raw.StartsWith('-'))
            lines.Add(new DiffLine(DiffLineKind.Delete, oldNo++, null, raw[1..]));
        else if (raw.StartsWith(' '))
            lines.Add(new DiffLine(DiffLineKind.Context, oldNo++, newNo++, raw[1..]));
        else
            return false;
        return true;
    }

    private static DiffCard? FromPairs(string? toolName, string? argumentsJson, JsonElement? resultMeta)
    {
        if (TryParseJson(argumentsJson) is { } arguments
            && FindPair(arguments) is { } pair)
        {
            var path = PathNames.Select(name => ReadString(arguments, name)).FirstOrDefault(value => value is not null);
            var title = toolName is null ? "diff" : path is null ? toolName : $"{toolName} {path}";
            return BuildPairCard(title, pair);
        }
        if (resultMeta is { ValueKind: JsonValueKind.Object } meta && FindPair(meta) is { } metaPair)
            return BuildPairCard(toolName ?? "diff", metaPair);
        return null;
    }

    private static DiffCard? BuildPairCard(string title, (string? Old, string? New) pair)
    {
        var lines = DiffLines(pair.Old, pair.New);
        return HasChanges(lines) ? new DiffCard(title, "pairs", lines) : null;
    }

    private static JsonElement? TryParseJson(string? json)
    {
        if (json is null)
            return null;
        try
        {
            var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? Old, string? New)? FindPair(JsonElement element)
    {
        foreach (var (oldName, newName) in PairNames)
        {
            string? oldValue = null;
            string? newValue = null;
            var oldFound = false;
            var newFound = false;
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    continue;
                if (string.Equals(property.Name, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    oldValue = property.Value.GetString();
                    oldFound = true;
                }
                else if (string.Equals(property.Name, newName, StringComparison.OrdinalIgnoreCase))
                {
                    newValue = property.Value.GetString();
                    newFound = true;
                }
            }
            if (oldFound || newFound)
                return (oldValue, newValue);
        }
        return null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasChanges(IReadOnlyList<DiffLine> lines)
    {
        foreach (var line in lines)
        {
            if (line.Kind is DiffLineKind.Add or DiffLineKind.Delete)
                return true;
        }
        return false;
    }
}

