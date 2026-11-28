namespace Dsh.Runtime;

/**
 * 会话后端的选择: `jsonl`(默认, 现状) 或 `database`(一个中心一个本地库)。
 *
 * 两个后端长期并存、由用户选: 选中 `database` 时由持久化插件在库不存在的那一次从 jsonl 目录自动导入。
 */
public sealed record SessionStorageSettings
{
    public const string PropName = "sessionStorage";

    public const string Jsonl = "jsonl";

    public const string DatabaseBackend = "database";

    public string Backend { get; init; } = Jsonl;

    /** 库文件; 留空用 `<持久化根>/sessions.db`, 相对路径相对持久化根。 */
    public string? Database { get; init; }

    /** 提交道数; 留空用 `min(4, CPU/2)`。 */
    public int? Lanes { get; init; }

    /** `sync`(默认) 或 `io_uring`(Linux 内核 ≥5.19, 不可用时自动退回)。 */
    public string Io { get; init; } = "sync";

    public bool UsesDatabase => string.Equals(Backend, DatabaseBackend, StringComparison.OrdinalIgnoreCase);

    public string ResolveDatabasePath(DshPaths paths)
        => Database is not { Length: > 0 } configured
            ? Path.GetFullPath(Path.Combine(paths.Root, "sessions.db"))
            : Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(paths.Root, configured));

    public static SessionStorageSettings FromContext(Context ctx)
        => ctx.GetProp(PropName) as SessionStorageSettings ?? new SessionStorageSettings();
}


