using Dsh.Lsp.Analysis;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSyntaxNode = Microsoft.CodeAnalysis.SyntaxNode;

namespace Dsh.Lsp.CSharp;

public sealed class CSharpDeclarationProvider : IDeclarationProvider
{
    public string Name => "roslyn-csharp-declarations";

    public Task<IReadOnlyList<StubDeclaration>> ExtractAsync(
        LspDocument document,
        CstNode? cst,
        SymbolGraph symbols,
        CancellationToken cancellationToken = default)
    {
        if (!document.LanguageId.Equals("csharp", StringComparison.OrdinalIgnoreCase)
            && !document.LanguageId.Equals("c#", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IReadOnlyList<StubDeclaration>>([]);

        var tree = CSharpSyntaxTree.ParseText(document.Text, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var declarations = root.DescendantNodes()
            .Where(node => node is BaseTypeDeclarationSyntax
                or DelegateDeclarationSyntax
                or BaseMethodDeclarationSyntax
                or BasePropertyDeclarationSyntax
                or EventFieldDeclarationSyntax
                or LocalFunctionStatementSyntax
                or EnumMemberDeclarationSyntax
                or UsingDirectiveSyntax
                or NamespaceDeclarationSyntax)
            .Select(node => Declaration(document, node))
            .Concat(root.DescendantNodes()
                .OfType<FieldDeclarationSyntax>()
                .SelectMany(field => field.Declaration.Variables.Select(variable => FieldDeclaration(document, variable))))
            .Where(declaration => declaration is not null)
            .Select(declaration => declaration!)
            .ToArray();
        return Task.FromResult<IReadOnlyList<StubDeclaration>>(declarations);
    }

    private static StubDeclaration? FieldDeclaration(LspDocument document, VariableDeclaratorSyntax variable)
    {
        var name = variable.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var start = document.PositionAt(variable.SpanStart);
        var end = document.PositionAt(variable.Span.End);
        var selectionStart = document.PositionAt(variable.Identifier.SpanStart);
        var selectionEnd = document.PositionAt(variable.Identifier.Span.End);
        return new StubDeclaration(
            $"{document.Uri}|{start.Line}:{start.Character}|{name}",
            name,
            "FieldDeclaration",
            document.Uri,
            new LspRange(start, end),
            new LspRange(selectionStart, selectionEnd),
            document.Version,
            "roslyn",
            ContainerId(document, variable),
            IsGenerated: document.Uri.Contains(".g.cs", StringComparison.OrdinalIgnoreCase));
    }

    private static StubDeclaration? Declaration(LspDocument document, RoslynSyntaxNode node)
    {
        var identifier = node switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier,
            DelegateDeclarationSyntax delegateSyntax => delegateSyntax.Identifier,
            ConstructorDeclarationSyntax constructor => constructor.Identifier,
            DestructorDeclarationSyntax destructor => destructor.Identifier,
            OperatorDeclarationSyntax @operator => @operator.OperatorToken,
            ConversionOperatorDeclarationSyntax conversion => conversion.Type.GetLastToken(),
            EventDeclarationSyntax @event => @event.Identifier,
            EventFieldDeclarationSyntax @event => @event.Declaration.Variables.FirstOrDefault()?.Identifier ?? default,
            LocalFunctionStatementSyntax local => local.Identifier,
            EnumMemberDeclarationSyntax member => member.Identifier,
            UsingDirectiveSyntax usingDirective => usingDirective.Name?.GetLastToken() ?? default,
            BaseMethodDeclarationSyntax method => method is MethodDeclarationSyntax named ? named.Identifier : default,
            BasePropertyDeclarationSyntax property => property is PropertyDeclarationSyntax named ? named.Identifier : default,
            NamespaceDeclarationSyntax ns => ns.Name.GetLastToken(),
            _ => default,
        };
        if (identifier == default && node is not NamespaceDeclarationSyntax)
            return null;
        var name = (node as NamespaceDeclarationSyntax)?.Name.ToString() ?? identifier.ValueText;
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var start = document.PositionAt(node.SpanStart);
        var end = document.PositionAt(node.Span.End);
        var selectionSpan = identifier.RawKind == 0 ? node.Span : identifier.Span;
        var selectionStart = document.PositionAt(selectionSpan.Start);
        var selectionEnd = document.PositionAt(selectionSpan.End);
        return new StubDeclaration(
            $"{document.Uri}|{start.Line}:{start.Character}|{name}",
            name,
            node.Kind().ToString(),
            document.Uri,
            new LspRange(start, end),
            new LspRange(selectionStart, selectionEnd),
            document.Version,
            "roslyn",
            ContainerId(document, node),
            IsPartial: node is BaseTypeDeclarationSyntax typeSyntax && typeSyntax.Modifiers.Any(SyntaxKind.PartialKeyword),
            Visibility: Visibility(node),
            GenericParameters: node.DescendantNodes().OfType<TypeParameterListSyntax>()
                .SelectMany(list => list.Parameters)
                .Select(parameter => parameter.Identifier.ValueText)
                .ToArray(),
            IsGenerated: document.Uri.Contains(".g.cs", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ContainerId(LspDocument document, RoslynSyntaxNode node)
    {
        var parent = node.Parent?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (parent is null)
            return null;
        var position = document.PositionAt(parent.SpanStart);
        return $"{document.Uri}|{position.Line}:{position.Character}|{parent.Identifier.ValueText}";
    }

    private static string? Visibility(RoslynSyntaxNode node)
    {
        var modifiers = node switch
        {
            BaseTypeDeclarationSyntax type => type.Modifiers,
            BaseMethodDeclarationSyntax method => method.Modifiers,
            BasePropertyDeclarationSyntax property => property.Modifiers,
            BaseFieldDeclarationSyntax field => field.Modifiers,
            _ => default,
        };
        foreach (var keyword in new[]
        {
            SyntaxKind.PublicKeyword,
            SyntaxKind.ProtectedKeyword,
            SyntaxKind.InternalKeyword,
            SyntaxKind.PrivateKeyword,
        })
        {
            if (modifiers.Any(keyword))
                return keyword.ToString().Replace("Keyword", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        }

        return null;
    }
}
