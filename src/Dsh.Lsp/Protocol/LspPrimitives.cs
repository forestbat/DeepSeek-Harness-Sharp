using System.Text.Json;

namespace Dsh.Lsp.Protocol;

/** Zero-based UTF-16 position, matching the LSP wire format. */
public readonly record struct LspPosition(int Line, int Character) : IComparable<LspPosition>
{
    public int CompareTo(LspPosition other)
    {
        var line = Line.CompareTo(other.Line);
        return line != 0 ? line : Character.CompareTo(other.Character);
    }

    public static LspPosition Zero => new(0, 0);
}

/** Half-open range. Start is inclusive, End is exclusive. */
public readonly record struct LspRange(LspPosition Start, LspPosition End)
{
    public bool IsEmpty => Start == End;

    public bool Contains(LspPosition position) => position.CompareTo(Start) >= 0 && position.CompareTo(End) < 0;

    public bool Contains(LspRange range) => range.Start.CompareTo(Start) >= 0 && range.End.CompareTo(End) <= 0;

    public bool Overlaps(LspRange other) => Start.CompareTo(other.End) < 0 && other.Start.CompareTo(End) < 0;
}

public sealed record LspLocation(string Uri, LspRange Range);

public sealed record LspTextChange(LspRange? Range, string Text, int? RangeLength = null)
{
    public static LspTextChange Full(string text) => new(null, text);

    public static LspTextChange Replace(LspRange range, string text, int? rangeLength = null)
        => new(range, text, rangeLength);
}

public enum LspDiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

public enum LspSaveReason
{
    Save = 1,
    Manual = 2,
    FocusOut = 3,
}

public enum LspFileEventType
{
    Created = 1,
    Changed = 2,
    Deleted = 3,
}

public sealed record LspFileEvent(string Uri, LspFileEventType Type);

public sealed record LspDiagnostic(
    LspRange Range,
    LspDiagnosticSeverity Severity,
    string? Code,
    string? Source,
    string Message,
    IReadOnlyList<string> Tags,
    JsonElement? Data);

public static class LspDocumentUri
{
    public static string FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return new Uri(fullPath, UriKind.Absolute).AbsoluteUri;
    }

    public static bool TryGetPath(string uri, out string path)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            path = parsed.LocalPath;
            return true;
        }

        path = string.Empty;
        return false;
    }

    public static string Normalize(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return uri;
        return parsed.AbsoluteUri;
    }
}
