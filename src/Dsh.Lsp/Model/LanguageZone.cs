namespace Dsh.Lsp.Model;

/** A caller-defined region parsed by a second language server. */
public sealed record LanguageZone(
    string Id,
    string Path,
    string LanguageId,
    string Text);

public sealed record ZoneAnalysis(
    LanguageZone Zone,
    DocumentAnalysis Analysis);
