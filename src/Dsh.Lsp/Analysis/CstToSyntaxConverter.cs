using Dsh.Lsp.Model;

namespace Dsh.Lsp.Analysis;

public static class CstToSyntaxConverter
{
    public static SyntaxNode Convert(CstNode node, int version, string? parentId = null)
    {
        var id = $"{parentId ?? "cst"}|{node.Range.Start.Line}:{node.Range.Start.Character}|{node.Type}";
        return new SyntaxNode(
            node.Type,
            node.FieldName,
            0,
            node.Range,
            node.Range,
            [],
            node.Children.Select(child => Convert(child, version, id)).ToArray(),
            Role: node.IsExtra ? "trivia" : "cst",
            IsError: node.IsError,
            DocumentVersion: version,
            Provenance: "parser",
            ParentId: parentId,
            Id: id);
    }
}
