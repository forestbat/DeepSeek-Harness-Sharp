using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Client;

public static class WorkspaceEditApplier
{
    public static TextEditApplication Apply(
        LspDocument document,
        IReadOnlyList<TextEditFact> edits)
    {
        var issues = new List<ValidationIssue>();
        var ordered = edits
            .Select(edit => (Edit: edit, Start: document.OffsetAt(edit.Range.Start), End: document.OffsetAt(edit.Range.End)))
            .OrderBy(item => item.Start)
            .ThenBy(item => item.End)
            .ToArray();
        var previousEnd = 0;
        foreach (var (edit, start, end) in ordered)
        {
            if (edit.Range.Start.CompareTo(edit.Range.End) > 0)
                issues.Add(new ValidationIssue("edit-range", "edit start is after its end", edit.Range));
            if (!IsPositionValid(document, edit.Range.Start) || !IsPositionValid(document, edit.Range.End))
                issues.Add(new ValidationIssue("edit-outside-document", "edit range is outside the document", edit.Range));
            if (start < previousEnd)
                issues.Add(new ValidationIssue("edit-overlap", "workspace edit contains overlapping text edits", edit.Range));
            previousEnd = Math.Max(previousEnd, end);
        }

        if (issues.Count > 0)
            return new TextEditApplication(document.Text, issues);

        var text = document.Text;
        foreach (var (edit, start, end) in ordered.OrderByDescending(item => item.Start))
            text = string.Concat(text.AsSpan(0, start), edit.NewText, text.AsSpan(end));
        return new TextEditApplication(text, []);
    }

    private static bool IsPositionValid(LspDocument document, LspPosition position)
        => position.Line >= 0
            && position.Line < document.LineCount
            && position.Character >= 0
            && position.Character <= document.LineLength(position.Line);
}
