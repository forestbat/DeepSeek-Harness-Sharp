namespace Dsh.Ide.History;

/** 工作区里检测到的 IDE 项目特征(可多选: 一个仓库可能同时有 .vscode/ 与 .idea/)。 */
[Flags]
public enum IdeWorkspaceKind
{
    None = 0,
    VsCode = 1,
    JetBrains = 2,
    Zed = 4,
}

public sealed record IdeHistoryEntry
{
    public required string Provider { get; init; }
    public required string Path { get; init; }
    public required long Timestamp { get; init; }
    public required string Kind { get; init; }
    public string? ContentId { get; init; }
    public string? EntryFile { get; init; }
    public string? StoreLocation { get; init; }
    public bool? MatchesGitHead { get; init; }
    public bool HasContent => ContentId is not null || EntryFile is not null;

    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp);
}

public sealed record IdeHistoryContent
{
    public required byte[] Bytes { get; init; }
    public required string Origin { get; init; }

    public string Text => System.Text.Encoding.UTF8.GetString(Bytes);
}

/**
 * 供 TUI/GUI 复用的 IDE 历史服务(§5): 与 checkpoints 无依赖, 关掉检查点也照常可用。
 * 查询按工作区门控(§3): 只有工作区带对应 IDE 标记时才查该 provider; 无标记返回空(不做全局扫描)。
 */
public interface IIdeHistoryService
{
    public const string ServiceName = "ideHistory";

    /** 当前工作区命中的 IDE 标记(无标记即 None)。 */
    IdeWorkspaceKind WorkspaceKind(string? workspaceRoot);

    /** 是否把 IDE 历史并进 `/timestamp` 时间线(插件参数 `timeline`, 默认 false)。 */
    bool TimelineEnabled { get; }

    /** 工作区内的历史条目(时间倒序, 上限见配置); 只查被工作区启用且配置允许的 provider。 */
    IReadOnlyList<IdeHistoryEntry> Query(string? workspaceRoot, long? from, long? to, string? path = null);

    /** 读某个条目的内容(供 revert 写回工作区)。 */
    IdeHistoryContent? Read(IdeHistoryEntry entry);
}

/** A bounded time window retaining every independently readable source revision. */
public sealed record IdeHistoryMoment(long Timestamp, IReadOnlyList<IdeHistoryEntry> Entries);

public interface IIdeHistoryTimeline
{
    IReadOnlyList<IdeHistoryMoment> QueryTimeline(string workspaceRoot, long? from = null, long? to = null);
}

public sealed record IdeRestorePreview(
    string WorkspaceRoot,
    IdeHistoryEntry Entry,
    IdeHistoryContent Content,
    bool TargetExists,
    string CurrentDigest,
    bool ModifiedSinceEntry,
    string Diff);

public sealed record IdeRestoreResult(bool Restored, string? BackupPath = null, string? Failure = null);

/** Preview and restore are separate so every surface can obtain explicit user consent. */
public interface IIdeHistoryRestore
{
    IdeRestorePreview PreviewRestore(string workspaceRoot, IdeHistoryEntry entry);
    IdeRestoreResult Restore(IdeRestorePreview preview, bool confirmModified);
}
