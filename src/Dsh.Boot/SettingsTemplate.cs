using Dsh.Plugins;

namespace Dsh.Boot;

/**
 * settings.yaml 模板生成块: 键和默认值来自 schema, 键上方的说明注释由模板保留。
 * `--write-template` 会更新缺失/变更的键, 但不会覆盖人工写的说明。
 */
public static class SettingsTemplate
{
    public static string DefaultPath
        => Path.Combine(AppContext.BaseDirectory, "Profiles", "Templates", "settings.yaml");

    public static string FixHint => "dsharp config schema --write-template --path <模板路径>";

    /** 校验生成块的结构(键/默认值/顺序)，说明注释不参与比较。 */
    public static string? Verify(string path)
    {
        if (!File.Exists(path))
            return $"模板不存在: {path}";
        var text = File.ReadAllText(path);
        if (!TryFindBlock(text, out var begin, out var tail))
            return $"模板缺少生成块标记({SettingsSchemaCatalog.GeneratedBegin} / {SettingsSchemaCatalog.GeneratedEnd}): {path}";
        var actual = ParseBlock(text[begin..tail]).Keys;
        var expected = ParseBlock(SettingsSchemaCatalog.RenderTemplateBlock()).Keys;
        return actual.SequenceEqual(expected)
            ? null
            : $"settings.yaml 生成块与配置面不同步: {path}; 修复: {FixHint}";
    }

    /** 重写生成块，保留已有键上方的说明注释。 */
    public static bool Write(string path)
    {
        var text = File.ReadAllText(path);
        if (!TryFindBlock(text, out var begin, out var tail))
            throw new InvalidOperationException($"模板缺少生成块标记({SettingsSchemaCatalog.GeneratedBegin} / {SettingsSchemaCatalog.GeneratedEnd}): {path}");
        var comments = ParseBlock(text[begin..tail]).Comments;
        var rest = tail;
        while (rest < text.Length && (text[rest] == '\r' || text[rest] == '\n'))
            rest++;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var updated = string.Concat(text[..begin], SettingsSchemaCatalog.RenderTemplateBlock(comments), newline, newline, text[rest..]);
        if (Normalize(updated) == Normalize(text))
            return false;
        File.WriteAllText(path, updated);
        return true;
    }

    private static BlockParse ParseBlock(string block)
    {
        var comments = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new List<string>();
        var document = SettingsSchemaCatalog.Document()?.Settings ?? [];
        var plugins = SettingsSchemaCatalog.Plugins().ToDictionary(plugin => plugin.Package, plugin => plugin.Settings, StringComparer.Ordinal);
        var stack = new List<Frame> { new(0, "", document) };
        var pending = new List<string>();
        foreach (var raw in block.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (!line.StartsWith("# ", StringComparison.Ordinal))
            {
                pending.Clear();
                continue;
            }
            var body = line[2..];
            if (body.Length == 0 || body == "(无参数)")
            {
                pending.Clear();
                continue;
            }
            if (body.StartsWith("顶层 settings", StringComparison.Ordinal)
                || body.StartsWith("插件参数", StringComparison.Ordinal)
                || body.StartsWith(">>>", StringComparison.Ordinal)
                || body.StartsWith("<<<", StringComparison.Ordinal))
            {
                pending.Clear();
                continue;
            }
            if (body.StartsWith('@') && body.EndsWith(':') && !body.Contains(' '))
            {
                var package = body[..^1];
                if (plugins.TryGetValue(package, out var settings))
                {
                    if (pending.Count > 0)
                        comments[package] = string.Join("\n", pending);
                    pending.Clear();
                    stack.Clear();
                    stack.Add(new Frame(2, package, settings));
                }
                continue;
            }
            if (IsKeyLine(body, out var indent, out var key))
            {
                var index = FindFrame(stack, indent, key);
                if (index >= 0)
                {
                    var frame = stack[index];
                    stack.RemoveRange(index + 1, stack.Count - index - 1);
                    var path = frame.Path.Length == 0 ? key : $"{frame.Path}.{key}";
                    if (pending.Count > 0)
                        comments[path] = string.Join("\n", pending);
                    pending.Clear();
                    keys.Add(body.Trim());
                    if (frame.Children.First(setting => setting.Key == key).Children is { Count: > 0 } children)
                        stack.Add(new Frame(indent + 2, path, children));
                    continue;
                }
            }
            pending.Add(body.Trim());
        }
        return new(comments, keys);
    }

    private static bool IsKeyLine(string body, out int indent, out string key)
    {
        var trimmed = body.TrimStart();
        indent = body.Length - trimmed.Length;
        var colon = trimmed.IndexOf(':');
        key = colon > 0 ? trimmed[..colon] : "";
        return colon > 0 && key.Length > 0 && !key.Any(char.IsWhiteSpace);
    }

    private static int FindFrame(List<Frame> stack, int indent, string key)
    {
        for (var index = stack.Count - 1; index >= 0; index--)
            if (stack[index].Indent == indent && stack[index].Children.Any(setting => setting.Key == key))
                return index;
        return -1;
    }

    private static bool TryFindBlock(string text, out int begin, out int tail)
    {
        begin = text.IndexOf(SettingsSchemaCatalog.GeneratedBegin, StringComparison.Ordinal);
        tail = 0;
        if (begin < 0)
            return false;
        var end = text.IndexOf(SettingsSchemaCatalog.GeneratedEnd, begin, StringComparison.Ordinal);
        if (end < 0)
            return false;
        tail = end + SettingsSchemaCatalog.GeneratedEnd.Length;
        return true;
    }

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n").TrimEnd();

    private readonly record struct BlockParse(IReadOnlyDictionary<string, string> Comments, IReadOnlyList<string> Keys);

    private readonly record struct Frame(int Indent, string Path, IReadOnlyList<PluginSettingSpec> Children);
}
