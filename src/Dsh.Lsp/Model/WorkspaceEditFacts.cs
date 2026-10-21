using System.Text.Json;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record DocumentHighlightFact(LspRange Range, string Kind);

public sealed record RenameTarget(LspRange Range, string Placeholder);

public sealed record TextEditFact(LspRange Range, string NewText);

public sealed record DocumentEdits(string Uri, IReadOnlyList<TextEditFact> Edits, int? Version = null);

public enum WorkspaceFileOperationKind
{
    Create,
    Rename,
    Delete,
}

public sealed record WorkspaceFileOperation(
    WorkspaceFileOperationKind Kind,
    string Uri,
    string? NewUri = null,
    bool Overwrite = false,
    bool IgnoreIfExists = false);

public sealed record WorkspaceEditFact(
    IReadOnlyList<DocumentEdits> Documents,
    IReadOnlyList<WorkspaceFileOperation>? FileOperations = null);

public sealed record WorkspaceSymbolFact(
    string Name,
    string Kind,
    string? ContainerName,
    string Uri,
    LspRange Range);

public sealed record CodeActionFact(
    string Title,
    string? Kind,
    bool IsPreferred,
    bool IsDisabled,
    string? DisabledReason,
    WorkspaceEditFact? Edit,
    JsonElement? Data = null);
