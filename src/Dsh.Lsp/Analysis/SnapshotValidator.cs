using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

public static class SnapshotValidator
{
    public static ValidationResult Validate(LspDocument document, SyntaxSnapshot snapshot)
    {
        var issues = new List<ValidationIssue>();
        var identities = new HashSet<(int StartLine, int StartCharacter, int EndLine, int EndCharacter, string Name)>();
        foreach (var node in snapshot.Roots)
            ValidateNode(document, node, null, issues, identities);
        foreach (var chain in snapshot.SelectionChains ?? [])
        {
            foreach (var range in chain.Ranges)
                ValidateRange(document, range, "selectionRange", issues);
        }
        if (snapshot.Cst is { } cst)
        {
            ValidateRange(document, cst.Range, "cst.range", issues);
            if (cst.StartByte < 0 || cst.EndByte < cst.StartByte)
                issues.Add(new ValidationIssue("cst-bytes", "invalid CST byte range", cst.Range));
            foreach (var root in snapshot.ParserRoots ?? [])
            {
                if (!cst.Range.Contains(root.Range))
                    issues.Add(new ValidationIssue("cst-root-range", "parser root is outside CST", root.Range));
            }

            if (snapshot.ParserRoots is { Count: > 0 } parserRoots)
            {
                foreach (var match in snapshot.IdentityMatches)
                {
                    if (match.ParserNode is null)
                        issues.Add(new ValidationIssue(
                            "cst-identity",
                            $"LSP node '{match.LspNode.Name}' has no parser identity",
                            match.LspNode.Range));
                }
            }
        }
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
        List<ValidationIssue> issues,
        HashSet<(int StartLine, int StartCharacter, int EndLine, int EndCharacter, string Name)> identities)
    {
        ValidateRange(document, node.Range, "syntax.range", issues);
        ValidateRange(document, node.SelectionRange, "syntax.selectionRange", issues);
        if (!identities.Add((
            node.Range.Start.Line,
            node.Range.Start.Character,
            node.Range.End.Line,
            node.Range.End.Character,
            node.Name)))
        {
            issues.Add(new ValidationIssue(
                "duplicate-node",
                $"duplicate node identity for '{node.Name}'",
                node.Range));
        }
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
        for (var index = 0; index < node.Children.Count; index++)
        {
            var child = node.Children[index];
            for (var other = index + 1; other < node.Children.Count; other++)
            {
                if (child.Range.Overlaps(node.Children[other].Range))
                {
                    issues.Add(new ValidationIssue(
                        "sibling-overlap",
                        $"siblings '{child.Name}' and '{node.Children[other].Name}' overlap",
                        child.Range));
                }
            }

            ValidateNode(document, child, node, issues, identities);
        }
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
