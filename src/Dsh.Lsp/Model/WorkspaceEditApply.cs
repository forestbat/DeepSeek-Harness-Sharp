using System.Text.Json;

namespace Dsh.Lsp.Model;

public sealed record TextEditApplication(string Text, IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed record DocumentEditResult(
    string Uri,
    string? NewText,
    IReadOnlyList<ValidationIssue> Issues);

public sealed record CommandExecutionResult(string Command, JsonElement? Result, string? Error = null);

public sealed record WorkspaceEditApplyOptions(
    bool WriteToDisk = false,
    bool DryRun = false,
    bool AllowNonFileUris = true);

public sealed record WorkspaceEditApplyResult(
    IReadOnlyList<DocumentEditResult> Documents,
    bool DryRun = false,
    bool Conflicted = false,
    CommandExecutionResult? Command = null)
{
    public bool IsValid => Documents.All(document => document.Issues.Count == 0)
        && Command?.Error is null;
}
