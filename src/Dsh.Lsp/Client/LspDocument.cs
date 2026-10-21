using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Client;

/** Immutable text snapshot with a UTF-16 line map. Versions increase on every accepted change. */
public sealed class LspDocument
{
    private readonly int[] _lineStarts;

    private LspDocument(string uri, string languageId, string text, int version)
    {
        Uri = uri;
        LanguageId = languageId;
        Text = text;
        Version = version;
        _lineStarts = BuildLineStarts(text);
    }

    public string Uri { get; }

    public string LanguageId { get; }

    public string Text { get; }

    public int Version { get; }

    public int Length => Text.Length;

    public int LineCount => _lineStarts.Length;

    public static LspDocument Create(string uri, string languageId, string text, int version = 1)
        => new(LspDocumentUri.Normalize(uri), languageId, text, version);

    public LspPosition PositionAt(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        var line = Array.BinarySearch(_lineStarts, offset);
        if (line < 0)
            line = ~line - 1;
        return new LspPosition(line, offset - _lineStarts[line]);
    }

    public int OffsetAt(LspPosition position)
    {
        if (position.Line < 0)
            return 0;
        if (position.Line >= _lineStarts.Length)
            return Text.Length;
        var start = _lineStarts[position.Line];
        var end = LineContentEnd(position.Line);
        return Math.Clamp(start + position.Character, start, end);
    }

    public int LineLength(int line)
    {
        if (line < 0 || line >= _lineStarts.Length)
            return 0;
        return LineContentEnd(line) - _lineStarts[line];
    }

    public string Slice(LspRange range)
    {
        var start = OffsetAt(range.Start);
        var end = OffsetAt(range.End);
        return start >= end ? string.Empty : Text[start..end];
    }

    public bool Contains(LspRange range)
        => OffsetAt(range.Start) <= OffsetAt(range.End)
            && OffsetAt(range.Start) >= 0
            && OffsetAt(range.End) <= Text.Length;

    public LspDocumentUpdate Apply(IReadOnlyList<LspTextChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var text = Text;
        foreach (var change in changes)
        {
            if (change.Range is null)
            {
                text = change.Text;
                continue;
            }

            var current = new LspDocument(Uri, LanguageId, text, Version);
            var start = current.OffsetAt(change.Range.Value.Start);
            var end = current.OffsetAt(change.Range.Value.End);
            if (start > end)
                throw new InvalidOperationException($"invalid text change range: {change.Range.Value.Start}..{change.Range.Value.End}");
            text = string.Concat(text.AsSpan(0, start), change.Text, text.AsSpan(end));
        }

        var next = new LspDocument(Uri, LanguageId, text, Version + 1);
        return new LspDocumentUpdate(next, ChangedRange(Text, text));
    }

    private int LineContentEnd(int line)
    {
        var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : Text.Length;
        while (end > _lineStarts[line] && Text[end - 1] is '\r' or '\n')
            end--;
        return end;
    }

    private static LspRange ChangedRange(string before, string after)
    {
        var prefix = 0;
        var maxPrefix = Math.Min(before.Length, after.Length);
        while (prefix < maxPrefix && before[prefix] == after[prefix])
            prefix++;

        var suffix = 0;
        var maxSuffix = Math.Min(before.Length - prefix, after.Length - prefix);
        while (suffix < maxSuffix && before[^(suffix + 1)] == after[^(suffix + 1)])
            suffix++;

        var beforeMap = new LspDocument(string.Empty, string.Empty, before, 0);
        var afterMap = new LspDocument(string.Empty, string.Empty, after, 0);
        return new LspRange(
            beforeMap.PositionAt(prefix),
            afterMap.PositionAt(after.Length - suffix));
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                    index++;
                starts.Add(index + 1);
            }
            else if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return [.. starts];
    }
}

public sealed record LspDocumentUpdate(LspDocument Document, LspRange ChangedRange);

/** Tracks open documents and their monotonic LSP versions. */
public sealed class LspDocumentStore
{
    private readonly Dictionary<string, LspDocument> _documents = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyList<LspDocument> OpenDocuments
    {
        get
        {
            lock (_gate)
                return [.. _documents.Values];
        }
    }

    public bool TryGet(string uri, out LspDocument? document)
    {
        lock (_gate)
            return _documents.TryGetValue(LspDocumentUri.Normalize(uri), out document);
    }

    public LspDocument Open(string uri, string languageId, string text)
    {
        var normalized = LspDocumentUri.Normalize(uri);
        lock (_gate)
        {
            if (_documents.ContainsKey(normalized))
                throw new InvalidOperationException($"document '{normalized}' is already open");
            var document = LspDocument.Create(normalized, languageId, text);
            _documents.Add(normalized, document);
            return document;
        }
    }

    public LspDocumentUpdate Change(string uri, IReadOnlyList<LspTextChange> changes)
    {
        var normalized = LspDocumentUri.Normalize(uri);
        lock (_gate)
        {
            if (!_documents.TryGetValue(normalized, out var document))
                throw new InvalidOperationException($"document '{normalized}' is not open");
            var update = document.Apply(changes);
            _documents[normalized] = update.Document;
            return update;
        }
    }

    public LspDocument? Close(string uri)
    {
        var normalized = LspDocumentUri.Normalize(uri);
        lock (_gate)
        {
            if (!_documents.Remove(normalized, out var document))
                return null;
            return document;
        }
    }
}
