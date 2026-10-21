using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

public static class SnapshotValidator
{
    public static ValidationResult Validate(LspDocument document, SyntaxSnapshot snapshot)
    {
        var issues = new List<ValidationIssue>();
        foreach (var node in snapshot.Roots)
            ValidateNode(document, node, null, issues);
        foreach (var token in snapshot.SemanticTokens)
            ValidateToken(document, token, issues);
        foreach (var folding in snapshot.FoldingRanges)
            ValidateFolding(document, folding, issues);
        foreach (var diagnostic in snapshot.Diagnostics)
            ValidateRange(document, diagnostic.Range, "diagnostic.range", issues);
        return new ValidationResult(issues);
    }

    private static void ValidateNode(
        LspDocument document,
        SyntaxNode node,
        SyntaxNode? parent,
        List<ValidationIssue> issues)
    {
        ValidateRange(document, node.Range, "syntax.range", issues);
        ValidateRange(document, node.SelectionRange, "syntax.selectionRange", issues);
        if (!node.Range.Contains(node.SelectionRange))
            issues.Add(new ValidationIssue(
                "selection-outside-range",
                $"selection range of '{node.Name}' is outside its node range",
                node.SelectionRange));
        if (parent is not null && !parent.Range.Contains(node.Range))
            issues.Add(new ValidationIssue(
                "child-outside-parent",
                $"node '{node.Name}' is outside its parent '{parent.Name}'",
                node.Range));
        foreach (var child in node.Children)
            ValidateNode(document, child, node, issues);
    }

    private static void ValidateToken(LspDocument document, SemanticToken token, List<ValidationIssue> issues)
    {
        ValidateRange(document, token.Range, "semanticToken.range", issues);
        if (token.Range.Start.Line != token.Range.End.Line)
            issues.Add(new ValidationIssue(
                "token-multiline",
                "semantic token range must stay on one line",
                token.Range));
    }

    private static void ValidateFolding(LspDocument document, FoldingRange folding, List<ValidationIssue> issues)
    {
        if (folding.StartLine < 0 || folding.StartLine >= document.LineCount)
            issues.Add(new ValidationIssue(
                "folding-start-line",
                $"folding start line {folding.StartLine} is outside the document",
                null));
        if (folding.EndLine < 0 || folding.EndLine >= document.LineCount)
            issues.Add(new ValidationIssue(
                "folding-end-line",
                $"folding end line {folding.EndLine} is outside the document",
                null));
        if (folding.StartLine > folding.EndLine)
            issues.Add(new ValidationIssue(
                "folding-order",
                "folding start line is after its end line",
                null));
    }

    private static void ValidateRange(
        LspDocument document,
        LspRange range,
        string code,
        List<ValidationIssue> issues)
    {
        if (!IsPositionValid(document, range.Start) || !IsPositionValid(document, range.End))
            issues.Add(new ValidationIssue(code, "range position is outside the document", range));
        if (range.Start.CompareTo(range.End) > 0)
            issues.Add(new ValidationIssue(code, "range start is after its end", range));
    }

    private static bool IsPositionValid(LspDocument document, LspPosition position)
        => position.Line >= 0
            && position.Line < document.LineCount
            && position.Character >= 0
            && position.Character <= document.LineLength(position.Line);
}
