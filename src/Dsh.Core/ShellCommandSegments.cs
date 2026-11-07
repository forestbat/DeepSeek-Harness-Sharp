using System.Text;

namespace Dsh.Core;

/** Shell family of a command line: decides the escape character and whether command names are case-sensitive. */
public enum ShellDialect
{
    Posix,
    PowerShell,
}

/** Model-visible shell tools by name, with the dialect their `command` argument is written in. */
public static class ShellToolDialects
{
    public static IReadOnlyDictionary<string, ShellDialect> ByToolName { get; } = new Dictionary<string, ShellDialect>(StringComparer.Ordinal)
    {
        ["bash"] = ShellDialect.Posix,
        ["bash_persistent"] = ShellDialect.Posix,
        ["wsl"] = ShellDialect.Posix,
        ["pwsh"] = ShellDialect.PowerShell,
        ["pwsh_persistent"] = ShellDialect.PowerShell,
        ["powershell"] = ShellDialect.PowerShell,
    };
}

/**
 * Best-effort splitter that turns a command line into the simple commands it would run, each as a list of unquoted words.
 * It splits on control operators (; & | newline and parentheses) and also scans command substitutions and quoted multi-word arguments (bash -c, eval, ssh ...) as nested command lines.
 * It is not a shell: variables, aliases, functions, globs and encoded payloads are not expanded.
 */
public static class ShellCommandSegments
{
    private const int MaxNestingDepth = 8;

    public static IReadOnlyList<IReadOnlyList<string>> Split(string commandLine, ShellDialect dialect)
    {
        var segments = new List<IReadOnlyList<string>>();
        Collect(commandLine, dialect, 0, segments);
        return segments;
    }

    private static void Collect(string commandLine, ShellDialect dialect, int depth, List<IReadOnlyList<string>> segments)
    {
        if (depth > MaxNestingDepth)
            return;
        var scanner = new Scanner(commandLine, dialect);
        scanner.Run();
        segments.AddRange(scanner.Segments);
        foreach (var nested in scanner.Nested)
            Collect(nested, dialect, depth + 1, segments);
    }

    private sealed class Scanner(string text, ShellDialect dialect)
    {
        private readonly StringBuilder _word = new();
        private readonly List<string> _segment = [];
        private bool _inWord;
        private int _index;

        public List<IReadOnlyList<string>> Segments { get; } = [];

        /** Command substitution bodies and quoted strings containing whitespace, scanned again as command lines. */
        public List<string> Nested { get; } = [];

        private char Escape => dialect == ShellDialect.PowerShell ? '`' : '\\';

        private char Next => _index + 1 < text.Length ? text[_index + 1] : '\0';

        public void Run()
        {
            while (_index < text.Length)
                Step(text[_index]);
            EndSegment();
        }

        private void Step(char current)
        {
            if (current == Escape)
                AppendEscaped();
            else if (current is '\'' or '"')
                ReadQuoted(current);
            else if (current is '$' or '<' or '>' && Next == '(')
                ReadSubstitution();
            else if (current == '`' && dialect == ShellDialect.Posix)
                ReadBackticks();
            else
                ReadPlain(current);
        }

        private void ReadPlain(char current)
        {
            _index++;
            if (current is ';' or '&' or '|' or '\n' or '(' or ')')
                EndSegment();
            else if (char.IsWhiteSpace(current) || current is '<' or '>')
                EndWord();
            else
                Append(current);
        }

        private void AppendEscaped()
        {
            if (_index + 1 < text.Length)
                Append(text[_index + 1]);
            _index += 2;
        }

        private void ReadQuoted(char quote)
        {
            var content = new StringBuilder();
            _index++;
            while (_index < text.Length && text[_index] != quote)
            {
                if (quote == '"' && TryReadEmbedded())
                    continue;
                if (quote == '"' && text[_index] == Escape && _index + 1 < text.Length)
                    _index++;
                content.Append(text[_index]);
                _index++;
            }
            _index++;
            _inWord = true;
            _word.Append(content);
            if (content.ToString().Any(char.IsWhiteSpace))
                Nested.Add(content.ToString());
        }

        /** Double-quoted strings still run $(...) and, in POSIX shells, backticks. */
        private bool TryReadEmbedded()
        {
            if (text[_index] == '$' && Next == '(')
            {
                ReadSubstitution();
                return true;
            }
            if (text[_index] != '`' || dialect != ShellDialect.Posix)
                return false;
            ReadBackticks();
            return true;
        }

        private void ReadSubstitution()
        {
            var bodyStart = _index + 2;
            var close = FindClosingParenthesis(bodyStart);
            Nested.Add(text[bodyStart..close]);
            _index = close + 1;
        }

        private void ReadBackticks()
        {
            var close = text.IndexOf('`', _index + 1);
            var end = close < 0 ? text.Length : close;
            Nested.Add(text[(_index + 1)..end]);
            _index = end + 1;
        }

        private int FindClosingParenthesis(int start)
        {
            var depth = 1;
            for (var position = start; position < text.Length; position++)
            {
                switch (text[position])
                {
                    case '(':
                        depth++;
                        break;
                    case ')' when --depth == 0:
                        return position;
                    case '\'':
                    case '"':
                        position = SkipQuoted(position);
                        break;
                }
            }
            return text.Length;
        }

        private int SkipQuoted(int openQuote)
        {
            var close = text.IndexOf(text[openQuote], openQuote + 1);
            return close < 0 ? text.Length : close;
        }

        private void Append(char value)
        {
            _word.Append(value);
            _inWord = true;
        }

        private void EndWord()
        {
            if (!_inWord)
                return;
            _segment.Add(_word.ToString());
            _word.Clear();
            _inWord = false;
        }

        private void EndSegment()
        {
            EndWord();
            if (_segment.Count == 0)
                return;
            Segments.Add([.. _segment]);
            _segment.Clear();
        }
    }
}
