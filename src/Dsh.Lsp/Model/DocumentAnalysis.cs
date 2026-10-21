using Dsh.Lsp.Client;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record ValidationIssue(string Code, string Message, LspRange? Range);

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public static ValidationResult Empty { get; } = new([]);
}

public sealed record DocumentAnalysisOptions(
    int MaxSymbols = 200,
    int MaxConcurrency = 4,
    bool IncludeReferences = true,
    bool IncludeTypes = true,
    bool IncludeInlayHints = true,
    bool IncludeHierarchy = true,
    int MaxHierarchySymbols = 50,
    bool IncludeSignatures = true)
{
    public static DocumentAnalysisOptions Default { get; } = new();
}

public sealed record DocumentAnalysis(
    LspDocument Document,
    LspCapabilityProfile Capabilities,
    SyntaxSnapshot Syntax,
    SymbolGraph Symbols,
    ScopeGraph Scopes,
    HierarchyGraph Hierarchy,
    TypeFactsSnapshot Types,
    IReadOnlyList<LspDiagnostic> Diagnostics,
    ValidationResult Validation)
{
    public bool IsValid => Validation.IsValid;
}
