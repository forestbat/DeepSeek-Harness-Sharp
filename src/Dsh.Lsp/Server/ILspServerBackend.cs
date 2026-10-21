using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Server;

public interface ILspServerBackend : IAsyncDisposable
{
    Task<JsonElement?> RequestAsync(
        string uri,
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken = default);

    Task<DocumentAnalysis> AnalyzeAsync(
        string uri,
        string languageId,
        string text,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LspLocation>> DefinitionAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LspLocation>> DeclarationAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LspLocation>> TypeDefinitionAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LspLocation>> ImplementationAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        string uri,
        LspPosition position,
        bool includeDeclaration,
        CancellationToken cancellationToken = default);

    Task<string?> HoverAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SelectionChain>> SelectionChainsAsync(
        string uri,
        IReadOnlyList<LspPosition> positions,
        CancellationToken cancellationToken = default);

    Task<RenameTarget?> PrepareRenameAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default);

    Task<WorkspaceEditFact?> RenameAsync(
        string uri,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentHighlightFact>> HighlightsAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeActionFact>> CodeActionsAsync(
        string uri,
        LspRange range,
        IReadOnlyList<LspDiagnostic> diagnostics,
        CancellationToken cancellationToken = default);

    Task<CodeActionFact?> ResolveCodeActionAsync(
        string uri,
        JsonElement action,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceSymbolFact>> WorkspaceSymbolsAsync(
        string languageId,
        string query,
        CancellationToken cancellationToken = default);
}
