using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

/** A caller-defined region parsed by a second language server. */
public sealed record LanguageZone(
    string Id,
    string Path,
    string LanguageId,
    string Text,
    LspRange? ParentRange = null);

public sealed record ZoneAnalysis(
    LanguageZone Zone,
    DocumentAnalysis Analysis,
    ZoneMapping Mapping)
{
    public IReadOnlyList<LspDiagnostic> MappedDiagnostics
        => Analysis.Diagnostics
            .Select(diagnostic => diagnostic with { Range = Mapping.Map(diagnostic.Range) })
            .ToArray();
}

public sealed record ZoneMapping(LspRange ParentRange)
{
    public LspPosition Map(LspPosition position)
        => position.Line == 0
            ? new LspPosition(ParentRange.Start.Line, ParentRange.Start.Character + position.Character)
            : new LspPosition(ParentRange.Start.Line + position.Line, position.Character);

    public LspRange Map(LspRange range) => new(Map(range.Start), Map(range.End));
}
