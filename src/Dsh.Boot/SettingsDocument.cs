using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dsh.Plugins;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Dsh.Boot;

/** settings.yaml 的文本级编辑:按段替换(plugins 段 / 有变化的顶层段),保留其余内容与注释。 */
internal static partial class SettingsDocument
{
    private const string PluginsHeader = "plugins:";
    private const string Indent = "  ";

    private static readonly HashSet<string> ReservedScalars =
        new(["true", "false", "null", "~", "yes", "no", "on", "off"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9_./@\-]+$")]
    private static partial Regex PlainScalar();

    public static string ReplacePluginsBlock(string text, string block)
    {
        var newline = NewLineOf(text);
        var lines = SplitLines(text);
        var start = FindPluginsStart(lines);
        if (start < 0)
            return $"{text.TrimEnd()}{newline}{block.ReplaceLineEndings(newline)}{newline}";
        var end = BlockEnd(lines, start);
        return $"{string.Join(newline, lines[..start].Concat(SplitLines(block)).Concat(lines[end..])).TrimEnd()}{newline}";
    }

    /**
     * Merge top-level edits relative to the caller's saved snapshot, not the latest file snapshot.
     * Unchanged sections retain concurrent writers' contents, comments and unknown keys.
     */
    public static string MergeTopLevel(string text, string previous, string next)
    {
        var before = TopLevelBlocks(SplitLines(previous));
        var after = TopLevelBlocks(SplitLines(next));
        var newline = NewLineOf(text);
        var lines = SplitLines(text).ToList();
        foreach (var (key, block) in after)
        {
            if (before.GetValueOrDefault(key) != block)
                ReplaceTopLevelBlock(lines, key, SplitLines(block));
        }
        foreach (var key in before.Keys.Where(key => !after.ContainsKey(key)).ToList())
            ReplaceTopLevelBlock(lines, key, []);
        return $"{string.Join(newline, lines).TrimEnd()}{newline}";
    }

    /** Parser marks delimit edits; every byte outside the owned mapping child remains in place. */
    public static string ReplaceMappingChild(string text, string parent, string key, string? value)
    {
        var parser = new Parser(new StringReader(text));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        var root = ReadSpan(parser);
        parser.Consume<DocumentEnd>();
        parser.Consume<StreamEnd>();
        var owner = root.Children.SingleOrDefault(pair => pair.Key.Start is Scalar scalar && scalar.Value == parent);
        if (owner.Value is null || owner.Value.Start is Scalar)
            return value is null ? text : owner.Value is null
                ? InsertEntry(text, root, parent, $"{{{key}: {value}}}")
                : ReplaceRange(text, owner.Value.StartIndex, owner.Value.End, $"{{{key}: {value}}}");
        if (owner.Value.Start is not MappingStart)
            throw new InvalidDataException($"{parent} must be a YAML mapping.");
        var child = owner.Value.Children.SingleOrDefault(pair => pair.Key.Start is Scalar scalar && scalar.Value == key);
        if (child.Value is null)
            return value is null ? text : InsertEntry(text, owner.Value, key, value);
        if (value is not null)
            return ReplaceRange(text, child.Value.StartIndex, child.Value.End, value);
        if (owner.Value.Children.Count > 1)
            return RemoveEntry(text, owner.Value, child.Key);
        return root.Children.Count > 1
            ? RemoveEntry(text, root, owner.Key)
            : ReplaceRange(text, owner.Value.StartIndex, owner.Value.End, "{}");
    }

    private sealed record YamlSpan(ParsingEvent Start, int End, List<(YamlSpan Key, YamlSpan Value)> Children)
    {
        public int StartIndex => checked((int)Start.Start.Index);
    }

    /** Retain physical token bounds instead of block-end marks, which may point at the next sibling. */
    private static YamlSpan ReadSpan(IParser parser)
    {
        var start = parser.Consume<ParsingEvent>();
        var end = checked((int)start.End.Index);
        List<(YamlSpan Key, YamlSpan Value)> children = [];
        if (start is MappingStart mapping)
        {
            while (parser.Current is not MappingEnd)
                children.Add((ReadSpan(parser), ReadSpan(parser)));
            var closing = parser.Consume<MappingEnd>();
            end = mapping.Style == MappingStyle.Flow ? checked((int)closing.End.Index + 1) : children[^1].Value.End;
        }
        else if (start is SequenceStart sequence)
        {
            while (parser.Current is not SequenceEnd)
                end = ReadSpan(parser).End;
            var closing = parser.Consume<SequenceEnd>();
            if (sequence.Style == SequenceStyle.Flow)
                end = checked((int)closing.End.Index + 1);
        }
        return new YamlSpan(start, end, children);
    }

    private static string InsertEntry(string text, YamlSpan mapping, string key, string value)
    {
        if (mapping.Start is not MappingStart start)
            return ReplaceRange(text, mapping.StartIndex, mapping.End, $"{{{key}: {value}}}");
        if (start.Style == MappingStyle.Flow)
        {
            var separator = mapping.Children.Count == 0 || FlowSeparator(text, mapping.Children[^1].Value.End, mapping.End - 1) >= 0 ? "" : ", ";
            return text.Insert(mapping.End - 1, $"{separator}{key}: {value}");
        }
        var newline = NewLineOf(text);
        var indent = new string(' ', checked((int)mapping.Children[0].Key.Start.Start.Column - 1));
        var lineEnd = text.IndexOf('\n', mapping.End);
        return lineEnd < 0
            ? $"{text}{newline}{indent}{key}: {value}"
            : text.Insert(lineEnd + 1, $"{indent}{key}: {value}{newline}");
    }

    private static string RemoveEntry(string text, YamlSpan mapping, YamlSpan key)
    {
        var index = mapping.Children.FindIndex(pair => ReferenceEquals(pair.Key, key));
        var value = mapping.Children[index].Value;
        if (mapping.Start is MappingStart { Style: MappingStyle.Flow } && mapping.Children.Count > 1)
        {
            var gapStart = index == 0 ? value.End : mapping.Children[index - 1].Value.End;
            var gapEnd = index == 0 ? mapping.Children[1].Key.StartIndex : key.StartIndex;
            var comma = FlowSeparator(text, gapStart, gapEnd);
            if (comma >= value.End)
                text = text.Remove(comma, 1);
            else
                return ReplaceRange(text, key.StartIndex, value.End, "").Remove(comma, 1);
        }
        return ReplaceRange(text, key.StartIndex, value.End, "");
    }

    private static int FlowSeparator(string text, int start, int end)
    {
        var scanner = new Scanner(new StringReader(text));
        while (scanner.MoveNext())
        {
            if (scanner.Current is YamlDotNet.Core.Tokens.FlowEntry comma && comma.Start.Index >= start && comma.Start.Index < end)
                return checked((int)comma.Start.Index);
        }
        return -1;
    }

    /** Keep comments even when the replaced value is a block containing its own comment tokens. */
    private static string ReplaceRange(string text, int start, int end, string replacement)
    {
        var scanner = new Scanner(new StringReader(text), skipComments: false);
        var newline = NewLineOf(text);
        while (scanner.MoveNext())
        {
            if (scanner.Current is not YamlDotNet.Core.Tokens.Comment comment || comment.Start.Index < start || comment.Start.Index >= end)
                continue;
            replacement = $"{replacement} {text[checked((int)comment.Start.Index)..checked((int)comment.End.Index)]}{newline}";
        }
        return $"{text[..start]}{replacement}{text[end..]}";
    }

    private static Dictionary<string, string> TopLevelBlocks(string[] lines)
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            if (TopLevelKey(lines[index]) is not { } key)
                continue;
            var end = TopLevelBlockEnd(lines, index);
            blocks[key] = string.Join('\n', lines[index..(end + 1)]);
            index = end;
        }
        return blocks;
    }

    private static void ReplaceTopLevelBlock(List<string> lines, string key, string[] replacement)
    {
        var start = lines.FindIndex(line => TopLevelKey(line) == key);
        if (start >= 0)
        {
            lines.RemoveRange(start, TopLevelBlockEnd(lines, start) - start + 1);
            lines.InsertRange(start, replacement);
            return;
        }
        if (replacement.Length == 0)
            return;
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            lines.RemoveAt(lines.Count - 1);
        lines.Add("");
        lines.AddRange(replacement);
    }

    /** 列 0 的 "key:" 行;缩进行、列 0 的序列项 "- "、注释与文档标记都不是键。 */
    private static string? TopLevelKey(string line)
    {
        if (line.Length == 0 || line[0] is ' ' or '\t' or '#' or '-')
            return null;
        var colon = line.IndexOf(':');
        return colon > 0 ? line[..colon].Trim().Trim('"', '\'') : null;
    }

    /** 段尾是最后一个缩进行或列 0 序列项;段后的空行与列 0 注释归下一段,不随本段改写。 */
    private static int TopLevelBlockEnd(IReadOnlyList<string> lines, int start)
    {
        var last = start;
        for (var index = start + 1; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Trim().Length == 0 || line[0] == '#')
                continue;
            if (line[0] is not (' ' or '\t' or '-'))
                break;
            last = index;
        }
        return last;
    }

    public static string RenderPlugins(IReadOnlyDictionary<string, PluginSetting> plugins)
    {
        var builder = new StringBuilder(PluginsHeader);
        foreach (var (name, setting) in plugins)
            RenderPlugin(builder, name, setting);
        return builder.ToString();
    }

    /** A bare boolean when the entry only carries enabled; otherwise the {enabled, permissions, ...parameters} mapping. */
    private static void RenderPlugin(StringBuilder builder, string name, PluginSetting setting)
    {
        if (setting.Parameters.Count == 0 && setting.Permissions is null)
        {
            builder.Append('\n').Append(Indent).Append(Quote(name)).Append(": ").Append(Bool(setting.Enabled));
            return;
        }
        const string entryIndent = Indent + Indent;
        builder.Append('\n').Append(Indent).Append(Quote(name)).Append(':');
        builder.Append('\n').Append(entryIndent).Append(PluginSetting.EnabledKey).Append(": ").Append(Bool(setting.Enabled));
        if (setting.Permissions is { } permissions)
        {
            builder.Append('\n').Append(entryIndent).Append(PluginSetting.PermissionsKey).Append(':');
            foreach (var permission in Enum.GetValues<PluginPermission>())
            {
                builder.Append('\n').Append(entryIndent).Append(Indent).Append(PluginPermissions.KeyOf(permission))
                    .Append(": ").Append(Bool(permissions.Allows(permission)));
            }
        }
        foreach (var (key, value) in setting.Parameters)
        {
            builder.Append('\n').Append(entryIndent).Append(FormatKey(key)).Append(':');
            RenderValue(builder, value, entryIndent + Indent);
        }
    }

    /** 嵌套 mapping/list 逐层展开(插件参数可以带 gpu/window 这类子段), 标量走 FormatScalar。 */
    private static void RenderValue(StringBuilder builder, object? value, string indent)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> map:
                if (map.Count == 0)
                {
                    builder.Append(" {}");
                    return;
                }
                foreach (var (key, item) in map)
                {
                    builder.Append('\n').Append(indent).Append(FormatKey(key)).Append(':');
                    RenderValue(builder, item, indent + Indent);
                }
                return;
            case string text:
                builder.Append(' ').Append(FormatString(text));
                return;
            case IEnumerable<object?> sequence:
                {
                    var items = sequence.ToList();
                    if (items.Count == 0)
                    {
                        builder.Append(" []");
                        return;
                    }
                    foreach (var item in items)
                    {
                        builder.Append('\n').Append(indent).Append('-');
                        RenderValue(builder, item, indent + Indent);
                    }
                    return;
                }
            default:
                builder.Append(' ').Append(FormatScalar(value));
                return;
        }
    }

    private static int FindPluginsStart(string[] lines)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();
            if (line == PluginsHeader || line.StartsWith(PluginsHeader + " ", StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    private static int BlockEnd(string[] lines, int start)
    {
        var end = start + 1;
        while (end < lines.Length && IsBlankOrIndented(lines[end]))
            end++;
        return end;
    }

    private static bool IsBlankOrIndented(string line)
        => line.Length == 0 || line[0] is ' ' or '\t';

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Split('\n');

    /** 回写沿用原文件的换行风格: Windows 上编辑过的 settings.yaml 是 CRLF, 只改一段时其余行不能被改成 LF。 */
    private static string NewLineOf(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Quote(string name) => $"\"{Escape(name)}\"";

    private static string FormatKey(string key)
    {
        var plain = PlainScalar().IsMatch(key) && !ReservedScalars.Contains(key);
        return plain ? key : Quote(key);
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FormatScalar(object? value) => value switch
    {
        null => "null",
        bool flag => Bool(flag),
        long number => number.ToString(CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        string text => FormatString(text),
        _ => FormatString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
    };

    private static string FormatString(string text)
    {
        var plain = text.Length > 0
            && PlainScalar().IsMatch(text)
            && !ReservedScalars.Contains(text)
            && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        return plain ? text : Quote(text);
    }
}
