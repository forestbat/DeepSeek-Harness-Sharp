using Dsh.Lsp.Analysis;
using Dsh.Lsp.TreeSitter;
using Dsh.Runtime;

namespace Dsh.Lsp;

public sealed record LspProviderSnapshot(
    ISyntaxTreeProvider? SyntaxTree,
    IStructuredTypeProvider? StructuredType,
    IFlowProvider? Flow,
    IDeclarationProvider? Declarations,
    IReferenceProvider? References,
    IZoneProvider? Zone);

public sealed class LspProviderRegistry(Context ctx) : Service(ctx, ServiceName)
{
    public const string ServiceName = "lspProviders";

    private sealed record Entry(
        string Source,
        ISyntaxTreeProvider? SyntaxTree,
        IStructuredTypeProvider? StructuredType,
        IFlowProvider? Flow,
        IDeclarationProvider? Declarations,
        IReferenceProvider? References,
        IZoneProvider? Zone,
        IReadOnlyList<string> LanguageIds,
        int Priority);

    private readonly List<Entry> _entries = [];
    private long _version;

    public event Action? Changed;

    public long Version => Volatile.Read(ref _version);

    public IDisposable Register(
        string source,
        ISyntaxTreeProvider? syntaxTree = null,
        IStructuredTypeProvider? structuredType = null,
        IFlowProvider? flow = null,
        IDeclarationProvider? declarations = null,
        IReferenceProvider? references = null,
        IZoneProvider? zone = null,
        IReadOnlyList<string>? languageIds = null,
        int priority = 0)
    {
        var entry = new Entry(
            source,
            syntaxTree,
            structuredType,
            flow,
            declarations,
            references,
            zone,
            languageIds ?? [],
            priority);
        _entries.Add(entry);
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
        return new Subscription(() =>
        {
            if (_entries.Remove(entry))
            {
                Interlocked.Increment(ref _version);
                Changed?.Invoke();
            }
        });
    }

    public LspProviderSnapshot Snapshot()
    {
        var ordered = _entries.OrderByDescending(entry => entry.Priority).ToArray();
        var syntax = ordered
            .Select(entry => entry.SyntaxTree is null
                ? null
                : new ScopedSyntaxTreeProvider(entry.SyntaxTree, entry.LanguageIds) as ISyntaxTreeProvider)
            .Where(provider => provider is not null)
            .Select(provider => provider!)
            .ToArray();
        var types = ordered
            .Select(entry => entry.StructuredType is null
                ? null
                : new ScopedTypeProvider(entry.StructuredType, entry.LanguageIds) as IStructuredTypeProvider)
            .Where(provider => provider is not null)
            .Select(provider => provider!)
            .ToArray();
        var flows = ordered
            .Select(entry => entry.Flow is null
                ? null
                : new ScopedFlowProvider(entry.Flow, entry.LanguageIds) as IFlowProvider)
            .Where(provider => provider is not null)
            .Select(provider => provider!)
            .ToArray();
        var declarations = ordered
            .Select(entry => entry.Declarations is null
                ? null
                : new ScopedDeclarationProvider(entry.Declarations, entry.LanguageIds) as IDeclarationProvider)
            .Where(provider => provider is not null)
            .Select(provider => provider!)
            .ToArray();
        var references = ordered
            .Select(entry => entry.References is null
                ? null
                : new ScopedReferenceProvider(entry.References, entry.LanguageIds) as IReferenceProvider)
            .Where(provider => provider is not null)
            .Select(provider => provider!)
            .ToArray();
        var zones = ordered
            .Select(entry => entry.Zone is null
                ? null
                : new ScopedZoneProvider(entry.Zone, entry.LanguageIds) as IZoneProvider)
            .Where(provider => provider is not null)
            .Select(provider => provider!)
            .ToArray();
        return new LspProviderSnapshot(
            syntax.Length == 0 ? null : new CompositeSyntaxTreeProvider(syntax),
            types.Length == 0 ? null : new CompositeTypeProvider(types),
            flows.Length == 0 ? null : new CompositeFlowProvider(flows),
            declarations.Length == 0 ? null : new CompositeDeclarationProvider(declarations),
            references.Length == 0 ? null : new CompositeReferenceProvider(references),
            zones.Length == 0 ? null : new CompositeZoneProvider(zones));
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
