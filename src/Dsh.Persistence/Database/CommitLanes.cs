using System.Collections.Concurrent;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence.Database;

/** 首次落库时写进 `log` 的那一行: 归属、代数、头帧(其余头字段都在头帧里)。 */
internal sealed record LogRowInit(string OwnerNode, long Generation, byte[] HeaderFrame);

/** 一次提交: 一个会话的一段帧, 以及它期望接在哪个 `durability_seq` 之后。 */
internal sealed class CommitBatch
{
    public required SessionId SessionId { get; init; }
    public required long Generation { get; init; }
    public required long FirstSeq { get; init; }
    public required IReadOnlyList<EncodedFrame> Frames { get; init; }
    public required long EventCount { get; init; }
    public long ByteCount { get; init; }
    /** 会话第一次落库: 同一事务里插入 `log` 行。 */
    public LogRowInit? Init { get; init; }
    internal ManualResetEventSlim Done { get; } = new(false);
    internal Exception? Error { get; set; }
}

/** 提交道的运行参数: 道数、攒批窗口与批量上限。 */
public sealed record CommitLaneOptions(int LaneCount, int WindowMilliseconds, long MaxBatchBytes)
{
    public static CommitLaneOptions Default { get; } = new(
        LaneCount: Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)),
        WindowMilliseconds: 5,
        MaxBatchBytes: 8L * 1024 * 1024);
}

/**
 * 提交道: 按会话分片成 N 道, 每道一条专用连接、一个线程、一次事务。
 *
 * subagent 与主会话同在一个进程, 几百个会话同时落盘 —— 提交道的数量决定它们是并行 `COMMIT` 还是排队。
 * 同一会话永远落同一道, 道内因此没有同会话竞争, 道间不碰同一行; 同一道里攒进来的多个会话合成一个
 * `BEGIN CONCURRENT … COMMIT`, 单个会话的号过期只把它自己摘出去, 邻居照常提交。
 */
internal sealed class CommitLanes : IDisposable
{
    private readonly Lane[] _lanes;
    private long _commits;
    private long _batches;
    private bool _disposed;

    public CommitLanes(TursoDatabase database, CommitLaneOptions options)
    {
        if (options.LaneCount < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "commit lanes need at least one lane");
        _lanes = new Lane[options.LaneCount];
        for (var index = 0; index < _lanes.Length; index += 1)
            _lanes[index] = new Lane(database.Connect(5_000), options, this, index);
    }

    /** 提交次数(打点用): 一次事务一增。 */
    internal long CommitCount => Interlocked.Read(ref _commits);

    /** 提交过的批次数(打点用)。 */
    internal long BatchCount => Interlocked.Read(ref _batches);

    public int LaneCount => _lanes.Length;

    /** 提交一批并等它真正 `COMMIT`: `Flush()` 返回后事件必然已在库里。 */
    public void Commit(CommitBatch batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lanes[(uint)StringComparer.Ordinal.GetHashCode(batch.SessionId.Value) % (uint)_lanes.Length].Enqueue(batch);
        batch.Done.Wait();
        if (batch.Error is { } error)
            throw error;
    }

    internal void CountCommit(long batches)
    {
        Interlocked.Increment(ref _commits);
        Interlocked.Add(ref _batches, batches);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var lane in _lanes)
            lane.Dispose();
    }

    private sealed class Lane : IDisposable
    {
        private readonly TursoConnection connection;
        private readonly CommitLaneOptions options;
        private readonly CommitLanes owner;
        private readonly BlockingCollection<CommitBatch> _queue = [];
        private readonly Thread _thread;

        public Lane(TursoConnection connection, CommitLaneOptions options, CommitLanes owner, int index)
        {
            this.connection = connection;
            this.options = options;
            this.owner = owner;
            _thread = new Thread(Run) { IsBackground = true, Name = $"session-commit-lane-{index}" };
            // 线程**按需**启动: 只读打开的进程一条也提交不了, 不该为此常驻 4 条空转线程
            // (它们的阻塞等待还会把采样剖析里的栈全占满, 见 plans/长会话打开基准-2026-11-28.md)。
        }

        private volatile bool _started;

        private void EnsureStarted()
        {
            if (_started)
                return;
            lock (_queue)
            {
                if (_started)
                    return;
                _started = true;
                _thread.Start();
            }
        }

        private void Run()
        {
            var group = new List<CommitBatch>();
            while (!_queue.IsCompleted)
            {
                CommitBatch first;
                try
                {
                    first = _queue.Take();
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                group.Clear();
                group.Add(first);
                var bytes = first.ByteCount;
                var deadline = Environment.TickCount64 + options.WindowMilliseconds;
                while (bytes < options.MaxBatchBytes)
                {
                    var remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
                    if (!_queue.TryTake(out var next, remaining))
                        break;
                    group.Add(next);
                    bytes += next.ByteCount;
                }
                Execute(group);
            }
        }

        public void Enqueue(CommitBatch batch)
        {
            try
            {
                EnsureStarted();
                _queue.Add(batch);
            }
            catch (InvalidOperationException)
            {
                batch.Error = new SessionWriteBusyException(Environment.ProcessId, "the session database is shutting down");
                batch.Done.Set();
            }
        }

        /**
         * 一次事务提交攒进来的全部批次。
         *
         * 号对不上的会话在开事务**之前**就被摘出去(单独按 stale 失败), 于是"整批原子"与
         * "单会话失败不拖累邻居"在同一个事务里同时成立。
         */
        private void Execute(List<CommitBatch> group)
        {
            var active = new List<CommitBatch>(group.Count);
            try
            {
                foreach (var batch in group)
                {
                    if (batch.Init is not null)
                    {
                        active.Add(batch);
                        continue;
                    }
                    var current = connection.Scalar(
                        "SELECT durability_seq FROM log WHERE session_id = ?1 AND writable = 1",
                        statement => statement.BindText(1, batch.SessionId.Value));
                    if (current == batch.FirstSeq)
                        active.Add(batch);
                    else
                        batch.Error = new SessionNumberStaleException(Math.Max(current, 0), batch.FirstSeq);
                }
                if (active.Count == 0)
                    return;

                using var scope = connection.Exclusive();
                connection.Execute("BEGIN CONCURRENT");
                Write(active);
                connection.Execute("COMMIT");
                owner.CountCommit(active.Count);
            }
            catch (Exception error)
            {
                Rollback();
                foreach (var batch in active)
                    batch.Error ??= Map(error, batch);
            }
            finally
            {
                foreach (var batch in group)
                    batch.Done.Set();
            }
        }

        /** 先把每个会话的 CAS 更新做完, 再插帧: 更新失败的那个不写帧, 邻居照旧。 */
        private void Write(List<CommitBatch> active)
        {
            var written = new List<CommitBatch>(active.Count);
            foreach (var batch in active)
            {
                if (batch.Init is { } init)
                {
                    InsertLogRow(batch, init);
                    written.Add(batch);
                    continue;
                }
                var changed = connection.Execute(
                    "UPDATE log SET durability_seq = durability_seq + ?1, revision = revision + 1, frame_bytes = frame_bytes + ?2 " +
                    "WHERE session_id = ?3 AND durability_seq = ?4 AND writable = 1",
                    statement =>
                    {
                        statement.BindInt(1, batch.EventCount);
                        statement.BindInt(2, batch.ByteCount);
                        statement.BindText(3, batch.SessionId.Value);
                        statement.BindInt(4, batch.FirstSeq);
                    });
                if (changed == 1)
                    written.Add(batch);
                else
                    batch.Error = new SessionNumberStaleException(-1, batch.FirstSeq);
            }
            foreach (var batch in written)
            {
                foreach (var frame in batch.Frames)
                {
                    connection.Execute(
                        "INSERT INTO frame(session_id, generation, first_seq, event_count, min_replace_start, bytes) " +
                        "VALUES(?1, ?2, ?3, ?4, ?5, ?6)",
                        statement =>
                        {
                            statement.BindText(1, batch.SessionId.Value);
                            statement.BindInt(2, batch.Generation);
                            statement.BindInt(3, frame.FirstSeq);
                            statement.BindInt(4, frame.EventCount);
                            statement.BindInt(5, frame.MinReplaceStart);
                            statement.BindBlob(6, frame.Content);
                        });
                }
            }
        }

        private void InsertLogRow(CommitBatch batch, LogRowInit init)
        {
            connection.Execute(
                "INSERT INTO log(session_id, owner_node, generation, writable, durability_seq, frame_bytes, revision, header_frame) " +
                "VALUES(?1, ?2, ?3, 1, ?4, ?5, 0, ?6)",
                statement =>
                {
                    statement.BindText(1, batch.SessionId.Value);
                    statement.BindText(2, init.OwnerNode);
                    statement.BindInt(3, init.Generation);
                    statement.BindInt(4, batch.EventCount);
                    statement.BindInt(5, batch.ByteCount);
                    statement.BindBlob(6, init.HeaderFrame);
                });
        }

        private void Rollback()
        {
            try
            {
                connection.Execute("ROLLBACK");
            }
            catch (SessionDatabaseException)
            {
                // 事务可能根本没开起来(第一条语句就失败): 没有可回滚的状态。
            }
        }

        /** 驱动错误码在本地路径恒为一个值, 只能按消息文本分"忙/冲突"与"真坏了"。 */
        private static SessionPersistenceException Map(Exception error, CommitBatch batch)
            => error switch
            {
                SessionDatabaseException { IsTransient: true } transient => new SessionWriteBusyException(
                    Environment.ProcessId, $"{batch.SessionId}: {transient.Message}"),
                SessionDatabaseException database => new SessionPersistenceCorruptionException(
                    $"{batch.SessionId}: the session database rejected the batch: {database.Message}", database),
                _ => new SessionPersistenceCorruptionException(
                    $"{batch.SessionId}: the session database failed while committing: {error.Message}", error),
            };

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (_started)
                _thread.Join(TimeSpan.FromSeconds(5));
            connection.Dispose();
        }
    }
}




