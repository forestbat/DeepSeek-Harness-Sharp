using System.Text.Json;

namespace Dsh.Core;

/**
 * A blacklist pattern such as "rm -rf /*" matched against the simple commands of a command line.
 * A pattern segment matches when its first word names a command anywhere in a simple command (so wrappers like sudo, env, xargs or VAR=value prefixes do not hide it) and all of its remaining words appear after that command in any order.
 * POSIX short-flag clusters are compared letter by letter, so -rf also matches -r -f and -fr.
 * Every segment of a multi-command pattern must match some segment of the command line; an empty pattern matches every command line.
 */
public sealed class ShellCommandPattern
{
    private const string ToolPrefix = "tool:";

    private readonly IReadOnlyList<IReadOnlyList<string>> _segments;
    private readonly ShellDialect _dialect;
    private readonly StringComparer _comparer;

    private ShellCommandPattern(IReadOnlyList<IReadOnlyList<string>> segments, ShellDialect dialect)
    {
        _segments = segments;
        _dialect = dialect;
        _comparer = dialect == ShellDialect.PowerShell ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    public static ShellCommandPattern Parse(string pattern, ShellDialect dialect)
        => new(ShellCommandSegments.Split(pattern.Trim(), dialect), dialect);

    public bool Matches(IReadOnlyList<IReadOnlyList<string>> commandSegments)
        => _segments.All(pattern => commandSegments.Any(command => MatchesSegment(pattern, command)));

    private bool MatchesSegment(IReadOnlyList<string> pattern, IReadOnlyList<string> command)
    {
        for (var start = 0; start < command.Count; start++)
        {
            if (NamesMatch(pattern[0], command[start]) && ArgumentsMatch(pattern.Skip(1), command.Skip(start + 1)))
                return true;
        }
        return false;
    }

    /** A pattern name without a directory matches the command's file name, so /bin/rm and rm.exe count as rm. */
    private bool NamesMatch(string patternName, string word)
    {
        if (patternName.IndexOfAny(['/', '\\']) >= 0)
            return _comparer.Equals(patternName, word);
        var name = word[(word.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^".exe".Length];
        return _comparer.Equals(patternName, name);
    }

    private bool ArgumentsMatch(IEnumerable<string> patternArguments, IEnumerable<string> commandArguments)
    {
        var (patternFlags, patternWords) = Classify(patternArguments);
        var (commandFlags, commandWords) = Classify(commandArguments);
        return patternFlags.IsSubsetOf(commandFlags) && patternWords.IsSubsetOf(commandWords);
    }

    private (HashSet<char> ShortFlags, HashSet<string> Words) Classify(IEnumerable<string> arguments)
    {
        var flags = new HashSet<char>();
        var words = new HashSet<string>(_comparer);
        foreach (var argument in arguments)
        {
            if (IsShortFlagCluster(argument))
                flags.UnionWith(argument[1..]);
            else
                words.Add(argument);
        }
        return (flags, words);
    }

    /** PowerShell parameters such as -Recurse are whole names, so only POSIX clusters like -rf are split into letters. */
    private bool IsShortFlagCluster(string argument)
        => _dialect == ShellDialect.Posix
            && argument.Length > 1
            && argument[0] == '-'
            && argument.Skip(1).All(char.IsAsciiLetter);

    /** Whitelist and blacklist deliberately share the current tool/command grammar. */
    public static string? MatchingRule(IReadOnlyList<string>? rules, string toolName, JsonElement arguments)
    {
        if (rules is null || rules.Count == 0)
            return null;
        var command = ShellToolDialects.ByToolName.TryGetValue(toolName, out var dialect)
            && arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("command", out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var segments = command is null ? [] : ShellCommandSegments.Split(command, dialect);
        return rules.OrderBy(rule => rule.Length).Select(SafetyRule.Parse)
            .FirstOrDefault(rule => rule.Matches(toolName, segments))?.Text;
    }

    /**
     * "tool:<name>" blocks a tool by name.
     * "<shell tool>:<pattern>" (e.g. "bash:", "pwsh:") blocks matching commands in every shell tool of that tool's dialect, so a POSIX rule also covers bash_persistent and wsl.
     */
    private sealed record SafetyRule(string Text, string? ToolName, ShellDialect? Dialect, ShellCommandPattern? Pattern)
    {
        public static SafetyRule Parse(string text)
        {
            if (text.StartsWith(ToolPrefix, StringComparison.OrdinalIgnoreCase))
                return new SafetyRule(text, text[ToolPrefix.Length..], null, null);
            foreach (var (tool, dialect) in ShellToolDialects.ByToolName)
            {
                if (text.StartsWith($"{tool}:", StringComparison.OrdinalIgnoreCase))
                    return new SafetyRule(text, null, dialect, ShellCommandPattern.Parse(text[(tool.Length + 1)..], dialect));
            }
            return new SafetyRule(text, null, null, null);
        }

        public bool Matches(string toolName, IReadOnlyList<IReadOnlyList<string>> segments)
            => Pattern is null
                ? ToolName == toolName
                : ShellToolDialects.ByToolName.TryGetValue(toolName, out var dialect) && dialect == Dialect && Pattern.Matches(segments);
    }
}
