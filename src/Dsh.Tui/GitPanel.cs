namespace Dsh.Tui;

/**
 * 右栏"Git 变更"的数据整理: 只负责解析/排序/截断, 进程调用留在 ChatWindow。
 * 做成纯函数以便单测; 输入是 `git diff --numstat` 的原始输出。
 */
public static class GitPanel
{
    public const int MaxRows = 12;

    /** numstat 行(`add\tdel\tpath`, 二进制为 `-\t-\tpath`)→ `{path} {add}+ {del}-`; 首行是来源。 */
    public static IReadOnlyList<string> Format(string numstat, string source)
    {
        var rows = new List<(string Path, string Text)>();
        foreach (var raw in numstat.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var parts = line.Split('\t');
            if (parts.Length < 3)
                continue;
            var path = parts[2];
            var binary = parts[0] == "-" || parts[1] == "-";
            rows.Add((path, binary ? $"{path} (binary)" : $"{path} {Parse(parts[0])}+ {Parse(parts[1])}-"));
        }

        var ordered = rows.OrderBy(row => row.Path, StringComparer.Ordinal).ToList();
        var result = new List<string> { source };
        result.AddRange(ordered.Take(MaxRows).Select(row => row.Text));
        if (ordered.Count > MaxRows)
            result.Add($"… (+{ordered.Count - MaxRows})");
        return result;
    }

    private static int Parse(string value) => int.TryParse(value, out var number) ? number : 0;
}
