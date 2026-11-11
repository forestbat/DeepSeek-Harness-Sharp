namespace Dsh.Checkpoints.Contracts;

/** 某会话某条消息之前最近一次检查点在工作树里的位置(缺省即没有可用检查点)。 */
public sealed record CheckpointSnapshot(string GitDirectory, string WorkspaceRoot, string Revision);

/** 取检查点快照: checkpoints 插件按这个名字把实现方法注册进来, 编辑器审阅按需解析。 */
public delegate CheckpointSnapshot? CheckpointSnapshotLookup(string workspaceRoot, string sessionId, long beforeSeq);

public static class CheckpointReviewServices
{
    public const string Snapshot = "checkpointReview";
}
