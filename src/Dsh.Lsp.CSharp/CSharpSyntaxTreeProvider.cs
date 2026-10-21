using System.Text;
using System.Collections.Concurrent;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Dsh.Lsp.TreeSitter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynSyntaxNode = Microsoft.CodeAnalysis.SyntaxNode;

namespace Dsh.Lsp.CSharp;

public sealed class CSharpSyntaxTreeProvider : ISyntaxTreeProvider
{
    private readonly ConcurrentDictionary<string, (string Text, SyntaxTree Tree)> _cache = new(StringComparer.Ordinal);

    public string Name => "roslyn-csharp";

    public bool SupportsIncremental => true;

    public Task<CstNode?> TryParseAsync(
        LspDocument document,
        CancellationToken cancellationToken = default)
    {
        if (!document.LanguageId.Equals("csharp", StringComparison.OrdinalIgnoreCase)
            && !document.LanguageId.Equals("c#", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<CstNode?>(null);

        var tree = _cache.TryGetValue(document.Uri, out var cached)
            ? cached.Text == document.Text
                ? cached.Tree
                : cached.Tree.WithChangedText(SourceText.From(document.Text))
            : CSharpSyntaxTree.ParseText(
                document.Text,
                cancellationToken: cancellationToken);
        _cache[document.Uri] = (document.Text, tree);
        var root = tree.GetRoot(cancellationToken);
        var text = tree.GetText(cancellationToken);
        var bytes = ByteOffsets(document.Text);
        return Task.FromResult<CstNode?>(Convert(root, text, bytes));
    }

    private static CstNode Convert(RoslynSyntaxNode node, SourceText text, int[] bytes)
    {
        var children = new List<CstNode>();
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
            {
                children.Add(Convert(child.AsNode()!, text, bytes));
                continue;
            }

            var token = child.AsToken();
            AddTrivia(children, token.LeadingTrivia, text, bytes);
            children.Add(Leaf(token.Kind().ToString(), token.Span, token.IsMissing, false, text, bytes));
            AddTrivia(children, token.TrailingTrivia, text, bytes);
        }

        return new CstNode(
            node.Kind().ToString(),
            null,
            true,
            node.IsMissing,
            false,
            bytes[node.SpanStart],
            bytes[node.Span.End],
            Range(text, node.Span),
            children);
    }

    private static void AddTrivia(
        List<CstNode> children,
        SyntaxTriviaList trivia,
        SourceText text,
        int[] bytes)
    {
        foreach (var item in trivia)
        {
            if (item.Span.Length == 0)
                continue;
            children.Add(Leaf(
                item.Kind().ToString(),
                item.Span,
                false,
                true,
                text,
                bytes));
        }
    }

    private static CstNode Leaf(
        string type,
        TextSpan span,
        bool missing,
        bool extra,
        SourceText text,
        int[] bytes)
        => new(
            type,
            null,
            false,
            missing,
            extra,
            bytes[span.Start],
            bytes[span.End],
            Range(text, span),
            []);

    private static LspRange Range(SourceText text, TextSpan span)
        => new(Position(text, span.Start), Position(text, span.End));

    private static LspPosition Position(SourceText text, int offset)
    {
        var line = text.Lines.GetLineFromPosition(offset);
        return new LspPosition(line.LineNumber, offset - line.Start);
    }

    private static int[] ByteOffsets(string text)
    {
        var result = new int[text.Length + 1];
        var byteIndex = 0;
        var charIndex = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            for (var index = 0; index < rune.Utf16SequenceLength; index++)
                result[charIndex + index] = byteIndex;
            charIndex += rune.Utf16SequenceLength;
            byteIndex += rune.Utf8SequenceLength;
        }

        result[^1] = byteIndex;
        return result;
    }
}
