using System.Text.Json.Nodes;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Client;

public sealed record LspWorkspaceFolder(string Uri, string Name);

public sealed record LspClientOptions(
    string RootPath,
    string ClientName = "dsh-lsp",
    string ClientVersion = "0.2.0",
    IReadOnlyList<LspWorkspaceFolder>? WorkspaceFolders = null,
    JsonNode? InitializationOptions = null,
    JsonNode? Settings = null,
    TimeSpan? RequestTimeout = null)
{
    public string RootUri => LspDocumentUri.FromPath(RootPath);

    public IReadOnlyList<LspWorkspaceFolder> EffectiveWorkspaceFolders
        => WorkspaceFolders is { Count: > 0 }
            ? WorkspaceFolders
            : [new LspWorkspaceFolder(RootUri, Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath))))];
}
