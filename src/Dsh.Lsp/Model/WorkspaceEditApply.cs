namespace Dsh.Lsp.Model;

public sealed record TextEditApplication(string Text, IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed record DocumentEditResult(
    string Uri,
    string? NewText,
    IReadOnlyList<ValidationIssue> Issues);

public sealed record WorkspaceEditApplyResult(IReadOnlyList<DocumentEditResult> Documents)
{
    public bool IsValid => Documents.All(document => document.Issues.Count == 0);
}
