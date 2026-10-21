using System.Text.Json;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Workspace;

public static class WorkspaceIndexStore
{
    public static async Task SaveAsync(
        string path,
        WorkspaceIndex index,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);
        await using var stream = File.Create(fullPath);
        await JsonSerializer.SerializeAsync(
            stream,
            index.Snapshot(),
            DshLspJsonContext.Default.WorkspaceIndexSnapshot,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<WorkspaceIndex> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var snapshot = await JsonSerializer.DeserializeAsync(
            stream,
            DshLspJsonContext.Default.WorkspaceIndexSnapshot,
            cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"workspace index '{path}' is empty");
        return WorkspaceIndex.FromSnapshot(snapshot);
    }
}
