namespace Dsh.Core;

public sealed record EditorReviewFile(string Path, string Left, string Right);

public sealed record EditorCheckpointSnapshot(string GitDirectory, string WorkspaceRoot, string Revision);

public interface IEditorCheckpointReview
{
    public const string ServiceName = "checkpointReview";
    EditorCheckpointSnapshot? FindSnapshot(string workspaceRoot, string sessionId, long beforeSeq);
}
