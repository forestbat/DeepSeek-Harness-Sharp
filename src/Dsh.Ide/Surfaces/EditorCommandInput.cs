using System.Globalization;
using System.Text;
using Dsh.Core;

namespace Dsh.Ide.Surfaces;

public sealed record EditorCommandInput
{
    public string Action { get; init; } = "status";
    public string? Path { get; init; }
    public string? Editor { get; init; }
    public string? TokenFile { get; init; }
    public int Line { get; init; } = 1;
    public int Column { get; init; } = 1;
    public bool HasPosition { get; init; }
    public bool Pick { get; init; }
    public bool Print { get; init; }
    public bool DryRun { get; init; }
    public bool Changed { get; init; }
    public bool Git { get; init; }
    public bool Checkpoint { get; init; }

    public static EditorCommandInput Parse(string input)
    {
        var tokens = Tokenize(input);
        if (tokens.Count == 0)
            return new();
        var result = new EditorCommandInput { Action = tokens[0] };
        var positional = false;
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (!positional && token == "--")
            {
                positional = true;
                continue;
            }
            if (positional || !token.StartsWith("--", StringComparison.Ordinal))
            {
                if (result.Path is not null)
                    throw new ArgumentException("Quote paths containing spaces; /ide accepts one path.");
                result = result with { Path = token };
                continue;
            }
            if (!flags.Add(token))
                throw new ArgumentException($"Repeated IDE option '{token}'.");
            (result, index) = ReadOption(result, token, tokens, index);
        }
        Validate(result);
        return result;
    }

    private static (EditorCommandInput Input, int Index) ReadOption(EditorCommandInput input, string option, IReadOnlyList<string> tokens, int index)
    {
        if (option is "--editor" or "--line" or "--column" or "--token-file")
        {
            if (++index >= tokens.Count)
                throw new ArgumentException($"Missing value for {option}.");
            var value = tokens[index];
            var updated = option switch
            {
                "--editor" => input with { Editor = value },
                "--token-file" => input with { TokenFile = value },
                "--line" => input with { Line = Position(value), HasPosition = true },
                _ => input with { Column = Position(value), HasPosition = true },
            };
            return (updated, index);
        }
        var toggled = option switch
        {
            "--pick" => input with { Pick = true },
            "--print" => input with { Print = true },
            "--dry-run" => input with { DryRun = true },
            "--changed" => input with { Changed = true },
            "--git" => input with { Git = true },
            "--checkpoint" => input with { Checkpoint = true },
            _ => throw new ArgumentException($"Unknown IDE option '{option}'."),
        };
        return (toggled, index);
    }

    private static int Position(string value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var position) && position > 0
            ? position : throw new ArgumentException("IDE line and column must be positive integers.");

    private static void Validate(EditorCommandInput input)
    {
        if (input.Action is not ("open" or "diff" or "reveal" or "list" or "status" or "why" or "doctor" or "bridge" or "lsp"))
            throw new ArgumentException("Usage: /ide open [path] | list | status | why | doctor | diff --git|--checkpoint.");
        if (input.Pick && input.Editor is not null)
            throw new ArgumentException("Choose either --pick or --editor.");
        if (input.Git && input.Checkpoint)
            throw new ArgumentException("Choose either --git or --checkpoint.");
        if (input.Action == "diff" && !input.Git && !input.Checkpoint)
            throw new ArgumentException("IDE diff requires --git or --checkpoint.");
        if (input.Changed && input.Action != "open")
            throw new ArgumentException("--changed is available only with /ide open.");
        if (input.TokenFile is not null && input.Action != "bridge")
            throw new ArgumentException("--token-file is available only with /ide bridge.");
        if (input.Action == "bridge" && string.IsNullOrWhiteSpace(input.Path))
            throw new ArgumentException("Usage: /ide bridge ENDPOINT [--token-file PATH].");
    }

    public static IReadOnlyList<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        var quote = '\0';
        var started = false;
        for (var index = 0; index < input.Length; index++)
        {
            var current = input[index];
            if (quote != '\0')
            {
                if (current == quote)
                    quote = '\0';
                else if (current == '\\' && index + 1 < input.Length && input[index + 1] == quote)
                    token.Append(input[++index]);
                else
                    token.Append(current);
            }
            else if (current is '\'' or '"')
            {
                quote = current;
                started = true;
            }
            else if (char.IsWhiteSpace(current))
            {
                if (!started)
                    continue;
                tokens.Add(token.ToString());
                token.Clear();
                started = false;
            }
            else
            {
                token.Append(current);
                started = true;
            }
        }
        if (quote != '\0')
            throw new ArgumentException("Unterminated quoted IDE argument.");
        if (started)
            tokens.Add(token.ToString());
        return tokens;
    }

    public EditorRequest Request(string workspace, string? sessionId = null, string? turnId = null)
    {
        var path = Path is null ? workspace : System.IO.Path.GetFullPath(Path, workspace);
        var extension = System.IO.Path.GetExtension(path);
        var project = extension is ".sln" or ".slnx" or ".code-workspace" or ".iml" or ".cbp" || System.IO.Path.GetFileName(path) == ".project";
        return new()
        {
            WorkspaceRoot = workspace, Path = path, Editor = Editor, SessionId = sessionId, TurnId = turnId,
            Line = Line, Column = Column, DryRun = DryRun, Print = Print, RememberSelection = Pick,
            Action = Action == "reveal" ? EditorAction.Reveal : Directory.Exists(path) || project ? EditorAction.OpenWorkspace : HasPosition ? EditorAction.OpenAtPosition : EditorAction.OpenFile,
        };
    }
}
