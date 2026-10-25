using System.Globalization;

namespace Dsh.GitInspect;

/** Parser for `git log -z` with GitLogParser.Format: commits are NUL separated, fields are unit-separator (0x1F) separated. */
internal static class GitLogParser
{
    private const char FieldSeparator = '\u001f';
    private const int FieldCount = 6;

    /** %s is the subject line only, so no field can contain a newline or the separators. */
    public const string Format = "--format=%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s";

    public static IReadOnlyList<GitCommitSummary> Parse(string output)
        => output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.Trim('\n').Split(FieldSeparator, FieldCount))
            .Where(fields => fields.Length == FieldCount)
            .Select(fields => new GitCommitSummary(
                fields[0],
                fields[1],
                fields[2],
                fields[3],
                DateTimeOffset.Parse(fields[4], CultureInfo.InvariantCulture),
                fields[5]))
            .ToList();
}
