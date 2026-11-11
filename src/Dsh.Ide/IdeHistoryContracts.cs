using Dsh.Core;

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

/** A bounded time window retaining every independently readable source revision. */
public sealed record IdeHistoryMoment(long Timestamp, IReadOnlyList<IdeHistoryEntry> Entries);

public sealed record IdeRestorePreview(
    string WorkspaceRoot,
    IdeHistoryEntry Entry,
    IdeHistoryContent Content,
    bool TargetExists,
    string CurrentDigest,
    bool ModifiedSinceEntry,
    string Diff);

public sealed record IdeRestoreResult(bool Restored, string? BackupPath = null, string? Failure = null);
