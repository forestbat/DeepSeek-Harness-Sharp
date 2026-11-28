using System.Collections.Concurrent;
using System.Globalization;

namespace Dsh.Persistence.Database;

/** 打开本地库的全部选项。 */
public sealed record EventStoreOptions
{
    public TursoIoDriver Driver { get; init; } = TursoIoDriver.Sync;

    public CommitLaneOptions Lanes { get; init; } = CommitLaneOptions.Default;

    /** 读连接池大小: 连接不线程安全, 单连接会把几百个会话的读排到提交者身后。 */
    public int Readers { get; init; } = 4;

    /** 本节点身份; 为空时由库自己生成并记住(`meta.node_id`)。 */
    public string? NodeId { get; init; }

    public static EventStoreOptions Default { get; } = new();
}

/**
 * 一个中心的本地库: 事务、帧索引、派生缓存与身份都在这一份里。
 *
 * 写只经提交道(每道一条连接、一次事务); 读走一个小连接池。库文件被这一进程独占打开,
 * 第二个进程会在打开这一步被引擎拒绝 —— 那正是"一个中心一个库"的边界。
 */
internal sealed class EventStore : IDisposable
{
    private readonly TursoDatabase _database;
    private readonly ConcurrentBag<TursoConnection> _readers = [];
    private readonly SemaphoreSlim _readerSlots;
    private readonly object _adminGate = new();
    private TursoConnection? _admin;
    private bool _disposed;

    private EventStore(TursoDatabase database, EventStoreOptions options, string nodeId)
    {
        _database = database;
        NodeId = nodeId;
        Options = options;
        Lanes = new CommitLanes(database, options.Lanes);
        _readerSlots = new SemaphoreSlim(options.Readers, options.Readers);
        _admin = database.Connect(5_000);
    }

    public string NodeId { get; }

    public EventStoreOptions Options { get; }

    public CommitLanes Lanes { get; }

    public string EngineVersion => _database.EngineVersion;

    public static EventStore Open(string path, EventStoreOptions options, Action<string>? warn)
    {
        var database = TursoDatabase.Open(path, options.Driver, warn);
        try
        {
            using var setup = database.Connect(5_000);
            EventStoreSchema.Ensure(setup);
            var nodeId = options.NodeId ?? EnsureNodeIdentity(setup);
            var store = new EventStore(database, options, nodeId);
            store.Prepare();
            return store;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /** 建节点行并把归属同步到本节点: 打开即完成, 之后的写只经提交道。 */
    private void Prepare()
    {
        lock (_adminGate)
        {
            EnsureNode(_admin!);
            MarkWritable(_admin!);
        }
    }

    /** 本节点身份: 首次打开时生成一个 UUID 并记住, 之后恒为同一个(id 与传输无关)。 */
    private static string EnsureNodeIdentity(TursoConnection connection)
    {
        var existing = connection.ScalarText("SELECT value FROM meta WHERE key = 'node_id'");
        if (existing is { Length: > 0 })
            return existing;
        var created = Guid.NewGuid().ToString("N");
        connection.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES('node_id', ?1)",
            statement => statement.BindText(1, created));
        return created;
    }

    private void EnsureNode(TursoConnection connection)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var firstSeen = connection.Scalar("SELECT first_seen FROM node WHERE id = ?1",
            statement => statement.BindText(1, NodeId));
        connection.Execute(
            "INSERT OR REPLACE INTO node(id, kind, first_seen, last_seen, trusted) VALUES(?1, 'self', ?2, ?3, 1)",
            statement =>
            {
                statement.BindText(1, NodeId);
                statement.BindInt(2, firstSeen < 0 ? now : firstSeen);
                statement.BindInt(3, now);
            });
    }

    /** 归属由 `log` 行决定: 只有本节点拥有的日志可写, 别的节点留下的是只读投影。 */
    private void MarkWritable(TursoConnection connection)
        => connection.Execute("UPDATE log SET writable = CASE WHEN owner_node = ?1 THEN 1 ELSE 0 END",
            statement => statement.BindText(1, NodeId));

    /** 借一条读连接; 用完必须还(见 `Release`)。 */
    public ReaderLease RentReader()
    {
        _readerSlots.Wait();
        if (!_readers.TryTake(out var connection))
            connection = _database.Connect(5_000);
        return new ReaderLease(this, connection);
    }

    private void Release(TursoConnection connection)
    {
        if (_disposed)
            connection.Dispose();
        else
            _readers.Add(connection);
        try
        {
            _readerSlots.Release();
        }
        catch (ObjectDisposedException)
        {
            // 关闭时仍有人握着租约: 连接已随库一起关掉, 这里只是把租约还回去。
        }
    }

    /** 元数据写入(建/改名/删除/折叠检查点): 频率低, 走单独一条连接即可。 */
    public T Admin<T>(Func<TursoConnection, T> action)
    {
        lock (_adminGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return action(_admin!);
        }
    }

    public void Admin(Action<TursoConnection> action) => Admin<bool>(connection =>
    {
        action(connection);
        return true;
    });

    public void Commit(CommitBatch batch) => Lanes.Commit(batch);

    /** 备份与整库搬运: MVCC 下直接可用, 副本可独立打开。 */
    public void Backup(string target)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (directory is { Length: > 0 })
            Directory.CreateDirectory(directory);
        if (File.Exists(target))
            File.Delete(target);
        lock (_adminGate)
            _admin!.VacuumInto(target);
    }

    public static string FormatRevision(long revision, long durabilitySeq)
        => string.Create(CultureInfo.InvariantCulture, $"db:{revision}:{durabilitySeq}");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Lanes.Dispose();
        lock (_adminGate)
        {
            _admin?.Dispose();
            _admin = null;
        }
        foreach (var reader in _readers)
            reader.Dispose();
        _readers.Clear();
        _readerSlots.Dispose();
        _database.Dispose();
    }

    /** 读连接的租约: `using` 结束时归还。 */
    public readonly struct ReaderLease(EventStore store, TursoConnection connection) : IDisposable
    {
        public TursoConnection Connection { get; } = connection;

        public void Dispose() => store.Release(Connection);
    }
}




