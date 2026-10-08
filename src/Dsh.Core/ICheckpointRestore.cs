namespace Dsh.Core;

/**
 * 把工作树恢复到某条消息之前的检查点(D3 revert 的文件侧)。定义在 Core, 由 Dsh.Checkpoints 实现,
 * 这样 TUI 等模块无需依赖检查点插件即可请求"回退到 <= seq 的最近检查点"。
 */
public interface ICheckpointRestore
{
    public const string ServiceName = "checkpointRestore";

    /** 恢复到 Seq <= seq 的最近检查点; 没有可用检查点时返回 null(文件不动)。返回恢复到的 commit。 */
    Task<string?> RestoreToSeqAsync(string cwd, long seq, CancellationToken signal);
}
