using Dsh.Core;
using Dsh.GitInspect;
using Dsh.Runtime;
using Dsh.Transport;
using Dsh.Ide.Selection;

namespace Dsh.Ide.Surfaces;

public sealed class EditorReviewService(Context ctx, string snapshotRoot)
{
    private const int FileBufferSize = 4096;
    public string SnapshotRoot { get; } = Path.GetFullPath(snapshotRoot);

    public async Task<IReadOnlyList<EditorReviewFile>> PrepareGitAsync(string workspace, CancellationToken cancellationToken)
    {
        var root = await GitRevisionFiles.RootAsync(workspace, cancellationToken).ConfigureAwait(false);
        return await PrepareAsync(workspace, root, "HEAD", null, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<EditorReviewFile>> PrepareCheckpointAsync(Session session, string workspace, long beforeSeq, CancellationToken cancellationToken)
    {
        var checkpoints = ctx.Get<IEditorCheckpointReview>(IEditorCheckpointReview.ServiceName, false)
            ?? throw new InvalidOperationException("The checkpoints plugin is not available.");
        var snapshot = checkpoints.FindSnapshot(session.Header.Cwd ?? workspace, session.Id.Value, beforeSeq)
            ?? throw new InvalidOperationException("No checkpoint exists before the selected turn.");
        return PrepareAsync(workspace, snapshot.WorkspaceRoot, snapshot.Revision, snapshot.GitDirectory, cancellationToken);
    }

    private async Task<IReadOnlyList<EditorReviewFile>> PrepareAsync(string workspace, string root, string revision, string? gitDirectory, CancellationToken cancellationToken)
    {
        var changes = await GitRevisionFiles.ChangesAsync(root, revision, gitDirectory, cancellationToken).ConfigureAwait(false);
        var destination = Path.Combine(SnapshotRoot, Guid.NewGuid().ToString("N"));
        var result = new List<EditorReviewFile>();
        foreach (var change in changes)
        {
            var current = Path.GetFullPath(change.Path, root);
            if (!SessionWorkingDirectory.IsWithin(workspace, current) || change.Kind != GitChangeKind.Deleted && Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is null)
                continue;
            if (!EditorPaths.IsWithin(Path.GetDirectoryName(current)!, workspace))
                throw new UnauthorizedAccessException("A review target resolves outside the workspace.");
            var pair = await SnapshotAsync(root, revision, gitDirectory, change, current, destination, result.Count, cancellationToken).ConfigureAwait(false);
            result.Add(pair);
        }
        return result;
    }

    private static async Task<EditorReviewFile> SnapshotAsync(string root, string revision, string? gitDirectory, GitFileStat change, string current, string destination, int index, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(destination);
        else
            Directory.CreateDirectory(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var left = Path.Combine(destination, $"{index}-before{Path.GetExtension(current)}");
        var right = Path.Combine(destination, $"{index}-after{Path.GetExtension(current)}");
        await using (var output = OwnerOnlyFiles.Create(left))
        {
            if (change.Kind != GitChangeKind.Added)
                await GitRevisionFiles.CopyToAsync(root, revision, change.OriginalPath ?? change.Path, output, gitDirectory, cancellationToken).ConfigureAwait(false);
        }
        await CopyCurrentAsync(current, right, cancellationToken).ConfigureAwait(false);
        return new(current, left, right);
    }

    private static async Task CopyCurrentAsync(string current, string destination, CancellationToken cancellationToken)
    {
        var link = new FileInfo(current).LinkTarget;
        if (link is not null)
        {
            OwnerOnlyFiles.Write(destination, link);
            return;
        }
        await using var output = OwnerOnlyFiles.Create(destination);
        if (!File.Exists(current))
            return;
        await using var input = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: FileBufferSize, useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }
}
