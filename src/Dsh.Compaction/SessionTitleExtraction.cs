using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

public static class SessionTitleExtraction
{
    private const string TitlePrefix = "TITLE:";
    private const int MaxTitleLength = 30;

    public static (IReadOnlyList<ContentBlock> Summary, string? Title) Extract(IReadOnlyList<ContentBlock> summary)
    {
        var blocks = new List<ContentBlock>();
        string? title = null;
        foreach (var block in summary)
        {
            if (block is not TextBlock text)
            {
                blocks.Add(block);
                continue;
            }
            var kept = new List<string>();
            foreach (var line in text.Text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(TitlePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = trimmed[TitlePrefix.Length..].Trim().Trim('"', '\'', '。', '.');
                    if (candidate.Length > 0)
                        title = candidate.Length > MaxTitleLength ? candidate[..MaxTitleLength] : candidate;
                    continue;
                }
                kept.Add(line);
            }
            var remaining = string.Join('\n', kept).TrimEnd();
            if (remaining.Length > 0)
                blocks.Add(text with { Text = remaining });
        }
        return (blocks, title);
    }
}
