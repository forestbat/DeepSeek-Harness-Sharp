using Dsh.Lsp.Analysis;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Dsh.Lsp.TreeSitter;

namespace Dsh.Lsp;

internal static class ProviderScope
{
    public static bool Matches(IReadOnlyList<string> languageIds, string languageId)
        => languageIds.Count == 0
            || languageIds.Contains("*", StringComparer.OrdinalIgnoreCase)
            || languageIds.Contains(languageId, StringComparer.OrdinalIgnoreCase);
}

internal sealed class ScopedSyntaxTreeProvider(
    ISyntaxTreeProvider provider,
    IReadOnlyList<string> languageIds) : ISyntaxTreeProvider
{
    public string Name => provider.Name;
    public bool SupportsIncremental => provider.SupportsIncremental;

    public Task<CstNode?> TryParseAsync(LspDocument document, CancellationToken cancellationToken = default)
        => ProviderScope.Matches(languageIds, document.LanguageId)
            ? provider.TryParseAsync(document, cancellationToken)
            : Task.FromResult<CstNode?>(null);
}

internal sealed class ScopedTypeProvider(
    IStructuredTypeProvider provider,
    IReadOnlyList<string> languageIds) : IStructuredTypeProvider
{
    public string Name => provider.Name;

    public Task<TypeExpression?> ResolveAsync(
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
        => ProviderScope.Matches(languageIds, document.LanguageId)
            ? provider.ResolveAsync(document, range, cancellationToken)
            : Task.FromResult<TypeExpression?>(null);
}

internal sealed class ScopedFlowProvider(
    IFlowProvider provider,
    IReadOnlyList<string> languageIds) : IFlowProvider
{
    public string Name => provider.Name;

    public Task<FlowGraph?> BuildAsync(
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
        => ProviderScope.Matches(languageIds, document.LanguageId)
            ? provider.BuildAsync(document, range, cancellationToken)
            : Task.FromResult<FlowGraph?>(null);
}

internal sealed class ScopedDeclarationProvider(
    IDeclarationProvider provider,
    IReadOnlyList<string> languageIds) : IDeclarationProvider
{
    public string Name => provider.Name;

    public Task<IReadOnlyList<StubDeclaration>> ExtractAsync(
        LspDocument document,
        CstNode? cst,
        SymbolGraph symbols,
        CancellationToken cancellationToken = default)
        => ProviderScope.Matches(languageIds, document.LanguageId)
            ? provider.ExtractAsync(document, cst, symbols, cancellationToken)
            : Task.FromResult<IReadOnlyList<StubDeclaration>>([]);
}

internal sealed class ScopedReferenceProvider(
    IReferenceProvider provider,
    IReadOnlyList<string> languageIds) : IReferenceProvider
{
    public string Name => provider.Name;

    public Task<IReadOnlyList<SymbolEdge>> ResolveAsync(
        string rootPath,
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
        => ProviderScope.Matches(languageIds, document.LanguageId)
            ? provider.ResolveAsync(rootPath, document, range, cancellationToken)
            : Task.FromResult<IReadOnlyList<SymbolEdge>>([]);
}

internal sealed class CompositeTypeProvider(IReadOnlyList<IStructuredTypeProvider> providers) : IStructuredTypeProvider
{
    public string Name => string.Join('+', providers.Select(provider => provider.Name));

    public async Task<TypeExpression?> ResolveAsync(
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
    {
        foreach (var provider in providers)
        {
            if (await provider.ResolveAsync(document, range, cancellationToken).ConfigureAwait(false) is { } result)
                return result;
        }

        return null;
    }
}

internal sealed class CompositeFlowProvider(IReadOnlyList<IFlowProvider> providers) : IFlowProvider
{
    public string Name => string.Join('+', providers.Select(provider => provider.Name));

    public async Task<FlowGraph?> BuildAsync(
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
    {
        foreach (var provider in providers)
        {
            if (await provider.BuildAsync(document, range, cancellationToken).ConfigureAwait(false) is { } result)
                return result;
        }

        return null;
    }
}

internal sealed class CompositeDeclarationProvider(IReadOnlyList<IDeclarationProvider> providers) : IDeclarationProvider
{
    public string Name => string.Join('+', providers.Select(provider => provider.Name));

    public async Task<IReadOnlyList<StubDeclaration>> ExtractAsync(
        LspDocument document,
        CstNode? cst,
        SymbolGraph symbols,
        CancellationToken cancellationToken = default)
    {
        foreach (var provider in providers)
        {
            var result = await provider.ExtractAsync(document, cst, symbols, cancellationToken).ConfigureAwait(false);
            if (result.Count > 0)
                return result;
        }

        return [];
    }
}

internal sealed class CompositeReferenceProvider(IReadOnlyList<IReferenceProvider> providers) : IReferenceProvider
{
    public string Name => string.Join('+', providers.Select(provider => provider.Name));

    public async Task<IReadOnlyList<SymbolEdge>> ResolveAsync(
        string rootPath,
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
    {
        foreach (var provider in providers)
        {
            var result = await provider.ResolveAsync(rootPath, document, range, cancellationToken).ConfigureAwait(false);
            if (result.Count > 0)
                return result;
        }

        return [];
    }
}

internal sealed class ScopedZoneProvider(
    IZoneProvider provider,
    IReadOnlyList<string> languageIds) : IZoneProvider
{
    public IReadOnlyList<LanguageZone> Detect(LspDocument document, string profileId)
        => ProviderScope.Matches(languageIds, document.LanguageId)
            ? provider.Detect(document, profileId)
            : [];
}

internal sealed class CompositeZoneProvider(IReadOnlyList<IZoneProvider> providers) : IZoneProvider
{
    public IReadOnlyList<LanguageZone> Detect(LspDocument document, string profileId)
    {
        foreach (var provider in providers)
        {
            var zones = provider.Detect(document, profileId);
            if (zones.Count > 0)
                return zones;
        }

        return [];
    }
}
