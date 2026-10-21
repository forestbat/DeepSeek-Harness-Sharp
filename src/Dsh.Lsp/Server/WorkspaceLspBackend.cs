using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Dsh.Lsp.Workspace;
using System.Text.Json;

namespace Dsh.Lsp.Server;

/** Adapts the headless workspace service to the enhanced LSP server. */
public sealed class WorkspaceLspBackend(LspWorkspace workspace) : ILspServerBackend
{
    public LspWorkspace Workspace { get; } = workspace;

    public Task<DocumentAnalysis> AnalyzeAsync(
        string uri,
        string languageId,
        string text,
        CancellationToken cancellationToken = default)
        => Workspace.AnalyzeTextAsync(languageId, PathFor(uri), text, cancellationToken);

    public Task<IReadOnlyList<LspLocation>> DefinitionAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => Workspace.GetDefinitionAsync(PathFor(uri), position, cancellationToken);

    public Task<IReadOnlyList<LspLocation>> DeclarationAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => Workspace.GetDeclarationAsync(PathFor(uri), position, cancellationToken);

    public Task<IReadOnlyList<LspLocation>> TypeDefinitionAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => Workspace.GetTypeDefinitionAsync(PathFor(uri), position, cancellationToken);

    public Task<IReadOnlyList<LspLocation>> ImplementationAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => Workspace.GetImplementationAsync(PathFor(uri), position, cancellationToken);

    public Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        string uri,
        LspPosition position,
        bool includeDeclaration,
        CancellationToken cancellationToken = default)
        => Workspace.GetReferencesAsync(PathFor(uri), position, includeDeclaration, cancellationToken);

    public Task<string?> HoverAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => Workspace.GetHoverAsync(PathFor(uri), position, cancellationToken);

    public Task<IReadOnlyList<SelectionChain>> SelectionChainsAsync(
        string uri,
        IReadOnlyList<LspPosition> positions,
        CancellationToken cancellationToken = default)
        => Workspace.GetSelectionChainsAsync(PathFor(uri), positions, cancellationToken);

    public Task<RenameTarget?> PrepareRenameAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => Workspace.PrepareRenameAsync(PathFor(uri), position, cancellationToken);

    public Task<WorkspaceEditFact?> RenameAsync(
        string uri,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken = default)
        => Workspace.RenameAsync(PathFor(uri), position, newName, cancellationToken);

    public Task<IReadOnlyList<DocumentHighlightFact>> HighlightsAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => Workspace.GetHighlightsAsync(PathFor(uri), position, cancellationToken);

    public Task<IReadOnlyList<CodeActionFact>> CodeActionsAsync(
        string uri,
        LspRange range,
        IReadOnlyList<LspDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
        => Workspace.GetCodeActionsAsync(PathFor(uri), range, cancellationToken);

    public Task<CodeActionFact?> ResolveCodeActionAsync(
        string uri,
        JsonElement action,
        CancellationToken cancellationToken = default)
        => Workspace.ResolveCodeActionAsync(PathFor(uri), action, cancellationToken);

    public Task<IReadOnlyList<WorkspaceSymbolFact>> WorkspaceSymbolsAsync(
        string languageId,
        string query,
        CancellationToken cancellationToken = default)
        => Workspace.GetWorkspaceSymbolsAsync(languageId, query, cancellationToken);

    public ValueTask DisposeAsync() => Workspace.DisposeAsync();

    private string PathFor(string uri)
    {
        if (LspDocumentUri.TryGetPath(uri, out var path))
            return path;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(uri)))[..16];
        return Path.Combine(Workspace.RootPath, ".dsh-lsp", $"{key}.txt");
    }
}
