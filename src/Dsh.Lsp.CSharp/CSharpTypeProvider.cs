using Dsh.Lsp.Client;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynSyntaxNode = Microsoft.CodeAnalysis.SyntaxNode;

namespace Dsh.Lsp.CSharp;

public sealed class CSharpTypeProvider : IStructuredTypeProvider
{
    public string Name => "roslyn-csharp-types";

    public async Task<TypeExpression?> ResolveAsync(
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
    {
        if (!document.LanguageId.Equals("csharp", StringComparison.OrdinalIgnoreCase)
            && !document.LanguageId.Equals("c#", StringComparison.OrdinalIgnoreCase))
            return null;

        var tree = CSharpSyntaxTree.ParseText(document.Text, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var start = document.OffsetAt(range.Start);
        var end = Math.Max(start, document.OffsetAt(range.End));
        var node = root.FindNode(TextSpan.FromBounds(start, end), getInnermostNodeForTie: true);
        CSharpCompilation compilation;
        if (LspDocumentUri.TryGetPath(document.Uri, out var path) && File.Exists(path))
        {
            try
            {
                var context = await CSharpProjectContext.LoadAsync(
                    Path.GetDirectoryName(Path.GetFullPath(path))!,
                    cancellationToken).ConfigureAwait(false);
                compilation = context.CreateCompilation(tree);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                compilation = LocalCompilation(tree);
            }
        }
        else
        {
            compilation = LocalCompilation(tree);
        }
        var model = compilation.GetSemanticModel(tree);
        var symbolInfo = model.GetSymbolInfo(node, cancellationToken);
        var symbol = symbolInfo.Symbol
            ?? model.GetDeclaredSymbol(node, cancellationToken)
            ?? model.GetSymbolInfo(node.Parent ?? node, cancellationToken).Symbol;
        var (type, declared) = symbol switch
        {
            ILocalSymbol local => (local.Type, false),
            IFieldSymbol field => (field.Type, true),
            IPropertySymbol property => (property.Type, true),
            IParameterSymbol parameter => (parameter.Type, true),
            IMethodSymbol method => (method.ReturnType, true),
            INamedTypeSymbol named => (named, true),
            _ => (model.GetTypeInfo(node, cancellationToken).Type, false),
        };
        var expression = Type(type, declared);
        if (expression is not null && symbol is IMethodSymbol methodSymbol)
        {
            expression = expression with
            {
                OverloadSignatures = methodSymbol.ContainingType?
                    .GetMembers(methodSymbol.Name)
                    .OfType<IMethodSymbol>()
                    .Select(candidate => candidate.ToDisplayString())
                    .ToArray(),
            };
        }

        if (expression is not null && symbolInfo.CandidateSymbols.Length > 0)
        {
            var candidates = symbolInfo.CandidateSymbols
                .OfType<IMethodSymbol>()
                .Select(candidate => candidate.ToDisplayString())
                .ToArray();
            if (candidates.Length > 0)
                expression = expression with { OverloadSignatures = candidates };
        }

        if (expression is not null && type is not null)
        {
            var conversions = new List<string>();
            foreach (var special in new[]
            {
                SpecialType.System_Object,
                SpecialType.System_String,
                SpecialType.System_Boolean,
                SpecialType.System_Char,
                SpecialType.System_SByte,
                SpecialType.System_Byte,
                SpecialType.System_Int16,
                SpecialType.System_UInt16,
                SpecialType.System_Int32,
                SpecialType.System_UInt32,
                SpecialType.System_Int64,
                SpecialType.System_UInt64,
                SpecialType.System_Decimal,
                SpecialType.System_Single,
                SpecialType.System_Double,
            })
            {
                var conversion = compilation.ClassifyConversion(type, compilation.GetSpecialType(special));
                if (conversion.Exists && !conversion.IsIdentity)
                    conversions.Add(compilation.GetSpecialType(special).ToDisplayString());
            }

            expression = expression with { Conversions = conversions };
        }

        return expression;
    }

    private static TypeExpression? Type(ITypeSymbol? type, bool declared = false)
    {
        if (type is null)
            return null;
        var display = type.ToDisplayString();
        if (type is IArrayTypeSymbol array)
            return new TypeExpression(display, TypeExpressionKind.Array, Element: Type(array.ElementType), IsDeclared: declared, IsInferred: !declared);
        if (type is IPointerTypeSymbol pointer)
            return new TypeExpression(display, TypeExpressionKind.Pointer, Element: Type(pointer.PointedAtType), IsDeclared: declared, IsInferred: !declared);
        if (type is INamedTypeSymbol named)
        {
            if (named.SpecialType != SpecialType.None)
                return new TypeExpression(display, TypeExpressionKind.Primitive, display, IsDeclared: declared, IsInferred: !declared);
            if (named.IsTupleType)
                return new TypeExpression(
                    display,
                    TypeExpressionKind.Tuple,
                    Elements: named.TupleElements.Select(element => Type(element.Type)).Where(item => item is not null).ToArray()!,
                    IsDeclared: declared,
                    IsInferred: !declared);
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return new TypeExpression(display, TypeExpressionKind.Nullable, Element: Type(named.TypeArguments[0]), IsNullable: true, IsDeclared: declared, IsInferred: !declared);
            var kind = named.TypeArguments.Length > 0 ? TypeExpressionKind.Generic : TypeExpressionKind.Named;
            return new TypeExpression(
                display,
                kind,
                named.Name,
                Arguments: named.TypeArguments.Select(argument => Type(argument)).Where(item => item is not null).ToArray()!,
                IsNullable: named.NullableAnnotation == NullableAnnotation.Annotated,
                IsDeclared: declared,
                IsInferred: !declared,
                Members: named.GetMembers()
                    .Where(member => !member.IsImplicitlyDeclared)
                    .Take(100)
                    .Select(member => new TypeMember(
                        member.Name,
                        member.Kind.ToString(),
                        member is IFieldSymbol field ? Type(field.Type) : member is IPropertySymbol property ? Type(property.Type) : null))
                    .ToArray());
        }

        return new TypeExpression(display, TypeExpressionKind.Primitive, type.Name, IsDeclared: declared, IsInferred: !declared);
    }

    private static IReadOnlyList<MetadataReference> References()
    {
        var paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? [typeof(object).Assembly.Location];
        return paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    }

    private static CSharpCompilation LocalCompilation(SyntaxTree tree)
        => CSharpCompilation.Create(
            "Dsh.Lsp.CSharpProbe",
            [tree],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
