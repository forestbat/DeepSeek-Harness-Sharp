using Dsh.Lsp.Client;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record DocumentSnapshot(
    LspDocument Document,
    LspCapabilityProfile Capabilities,
    SyntaxSnapshot Syntax,
    IReadOnlyList<LspDiagnostic> Diagnostics,
    ValidationResult Validation,
    IReadOnlyList<ZoneAnalysis>? Zones = null)
{
    public bool IsValid => Validation.IsValid;
}
