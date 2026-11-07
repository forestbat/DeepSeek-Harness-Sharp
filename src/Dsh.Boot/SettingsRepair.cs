using YamlDotNet.Core;

namespace Dsh.Boot;

/**
 * settings.yaml 损坏时的段级修复: 逐层尝试"丢掉坏的那一条"，其余字节原样保留。
 * 丢弃单位是顶层段或顶层段下的一条 entry(如 mcp.phone / providers.my-provider / plugins."@pkg"); 再深就整条 entry 丢，
 * 不做更细的猜测 —— 半截配置比缺失配置更难排查。
 */
internal static class SettingsRepair
{
    /** 隔离文件的标记: settings.yaml.invalid-<时间戳>。 */
    internal const string QuarantineMarker = ".invalid-";

    /** 丢弃允许下降到的层数: 1 = 顶层段, 2 = 顶层段下的一条 entry。 */
    private const int MaxDropDepth = 2;

    /** 这些子树里的损坏不能靠丢弃来修: 丢弃等于把限制放宽(插件权限全开、沙箱关闭), 只能拒绝启动并指着行号说清楚。 */
    private static readonly string[] FailClosedPrefixes = ["plugins", "sandbox"];

    private readonly record struct Entry(string Key, int Start, int End);

    /** Text 为 null 表示不修: Refused=true 是"丢了会放宽权限", false 是"定位不出边界"。 */
    internal sealed record Attempt(string? Text, IReadOnlyList<string> Dropped, bool Refused, int Line, int Column, string Reason)
    {
        public string Describe(string path)
            => $"settings.yaml 第 {Line} 行 {Column} 列无法解析({Reason}); 已丢弃 "
               + $"{string.Join(", ", Dropped)} 并把原文保存为 {path}";

        public string Refusal
            => Refused
                ? $"丢弃 {string.Join(", ", Dropped)} 会放宽权限(插件权限/沙箱开关), 因此不自动修复"
                : "无法在不猜测的前提下修复";
    }

    internal static bool IsFailClosed(string path)
        => FailClosedPrefixes.Any(prefix => path == prefix || path.StartsWith(prefix + ".", StringComparison.Ordinal));

    /** 修复成功时返回保留原文换行风格的新文本; 失败由调用方决定回退(拒绝启动)。 */
    internal static Attempt Repair(string text, YamlException error)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var entries = SplitEntries(lines, 0, lines.Length);
        if (entries.Count == 0)
            return Refusal([], refused: false, error);
        var dropped = new List<string>();
        var kept = new List<string>(lines[..entries[0].Start]);
        foreach (var entry in entries)
            kept.AddRange(RepairEntry(lines, entry, entry.Key, depth: 1, dropped));
        if (dropped.Count == 0)
            return Refusal(dropped, refused: false, error);
        if (dropped.Any(IsFailClosed))
            return Refusal(dropped, refused: true, error);
        var repaired = string.Join(newline, kept);
        return HarnessSettings.TryValidate(repaired, out _)
            ? new Attempt(repaired, dropped, Refused: false, checked((int)error.Start.Line), checked((int)error.Start.Column), error.Message)
            : Refusal(dropped, refused: false, error);
    }

    private static Attempt Refusal(IReadOnlyList<string> dropped, bool refused, YamlException error)
        => new(null, dropped, refused, checked((int)error.Start.Line), checked((int)error.Start.Column), error.Message);

    private static string[] RepairEntry(string[] lines, Entry entry, string path, int depth, List<string> dropped)
    {
        var region = lines[entry.Start..entry.End];
        if (TryParse(region))
            return region;
        if (depth >= MaxDropDepth)
        {
            dropped.Add(path);
            return [];
        }
        var children = SplitEntries(lines, entry.Start + 1, entry.End);
        if (children.Count < 2)
        {
            dropped.Add(path);
            return [];
        }
        var mark = dropped.Count;
        var kept = new List<string> { lines[entry.Start] };
        foreach (var child in children)
            kept.AddRange(RepairEntry(lines, child, $"{path}.{child.Key}", depth + 1, dropped));
        if (dropped.Count > mark && TryParse(kept))
            return [.. kept];
        dropped.RemoveRange(mark, dropped.Count - mark);
        dropped.Add(path);
        return [];
    }

    /** 区域内的同缩进条目; 遇到序列项(- )、或无冒号的坏行就放弃切分, 交由上层整块处理。 */
    private static List<Entry> SplitEntries(string[] lines, int start, int end)
    {
        var entries = new List<Entry>();
        var baseIndent = -1;
        for (var index = start; index < end; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;
            if (trimmed[0] == '-')
                return [];
            var indent = line.Length - trimmed.Length;
            if (baseIndent < 0)
                baseIndent = indent;
            if (indent != baseIndent)
                continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                return [];
            if (entries.Count > 0)
                entries[^1] = entries[^1] with { End = index };
            var key = trimmed[..colon].Trim().Trim('"', '\'');
            if (key.Length == 0)
                return [];
            entries.Add(new Entry(key, index, end));
        }
        return entries;
    }

    private static bool TryParse(IReadOnlyList<string> lines)
    {
        if (lines.All(line => line.Trim().Length == 0 || line.TrimStart()[0] == '#'))
            return true;
        try
        {
            _ = HarnessSettings.Parse(string.Join('\n', lines));
            return true;
        }
        catch (YamlException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
