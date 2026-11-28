using System.Globalization;

namespace Dsh.Persistence.Database;

/**
 * 本地库的表结构: 一个中心一个库, 日志、帧、派生缓存与身份都在这一份里。
 *
 * `log` 只存"归属 + 计数 + 头帧": 时间、cwd、父子关系、种子长度这些字段本来就在头帧那行 JSON 里,
 * 再开一列等于把同一条事实存两遍(读的时候还要保证两边一致)。列表排序在内存里做,
 * 会话量是几百到几千, 不值得为它多一套列的维护。
 *
 * `PRIMARY KEY(session_id, first_seq)` 就是序号密度的闸: 同一段序号不可能被两个写者各写一次;
 * `frame` 表同时就是帧索引, 不再有 `.frames` 旁车。
 */
internal static class EventStoreSchema
{
    public const int Version = 1;

    /**
     * 建表脚本。全文搜索的位置在这里预留(与事件同库, 粒度是事件, 靠 seq 指回 frame):
     * 上游发布带 `fts` 的原生库之后再建, 现在建了也用不上。
     */
    public const string CreateScript = """
        CREATE TABLE IF NOT EXISTS meta (
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS node (
          id TEXT PRIMARY KEY,
          kind TEXT NOT NULL,
          display_name TEXT,
          public_key BLOB,
          first_seen INTEGER NOT NULL,
          last_seen INTEGER NOT NULL,
          trusted INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS log (
          session_id TEXT PRIMARY KEY,
          owner_node TEXT NOT NULL,
          generation INTEGER NOT NULL,
          writable INTEGER NOT NULL,
          durability_seq INTEGER NOT NULL,
          frame_bytes INTEGER NOT NULL DEFAULT 0,
          revision INTEGER NOT NULL DEFAULT 0,
          header_frame BLOB NOT NULL
        );
        CREATE TABLE IF NOT EXISTS frame (
          session_id TEXT NOT NULL,
          generation INTEGER NOT NULL,
          first_seq INTEGER NOT NULL,
          event_count INTEGER NOT NULL,
          min_replace_start INTEGER NOT NULL,
          bytes BLOB NOT NULL,
          PRIMARY KEY (session_id, first_seq)
        );
        CREATE TABLE IF NOT EXISTS segment (
          hash TEXT PRIMARY KEY,
          bytes BLOB NOT NULL,
          size INTEGER NOT NULL,
          created_at INTEGER NOT NULL,
          last_used INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS surface_fold (
          session_id TEXT PRIMARY KEY,
          generation INTEGER NOT NULL,
          event_count INTEGER NOT NULL,
          replace_generation INTEGER NOT NULL,
          nodes BLOB NOT NULL,
          fold_meta BLOB,
          updated_at INTEGER NOT NULL
        );
        """;

    /** 打开连接后的库级设置; `journal_mode` 设一次就持久化。 */
    public const string ConfigureScript = """
        PRAGMA journal_mode = mvcc;
        PRAGMA synchronous = full;
        """;

    public static void Ensure(TursoConnection connection)
    {
        connection.ExecuteScript(ConfigureScript);
        connection.ExecuteScript(CreateScript);
        var version = connection.Scalar("SELECT value FROM meta WHERE key = 'schema_version'", null);
        _ = version;
        connection.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES('schema_version', ?1)",
            statement => statement.BindText(1, Version.ToString(CultureInfo.InvariantCulture)));
    }
}
