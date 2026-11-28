using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence.Database;

/**
 * 数据库后端与共享帧格式(`SessionLogFrames`)的接缝: **行 ↔ 帧**的适配, 以及写不进去时的证据保留。
 *
 * 切片、导出字节、导入字节这些逻辑不在这里 —— 它们在共享层按"头帧 + 帧序列"写一次, 两个后端共用;
 * 这里只做数据库才有的事: 把一帧插成 `frame` 的一行, 或者把 `frame` 的行读成帧。
 */
public sealed partial class DatabaseSessionPersistence
{
    /**
     * 从 JSONL 目录导入全部会话: 帧与头帧**原样搬运**, 不解压、不重编码, 一次只在内存里留一帧。
     *
     * 已经导入过的会话(库里已有条数)跳过, 所以半途中断后再跑一次会接着往下导。
     * 返回实际导入的会话数。
     */
    public int ImportFrom(string jsonlRoot, Action<string>? progress = null)
    {
        using var source = new JsonlSessionPersistence(jsonlRoot, warn: Warn);
        var imported = 0;
        foreach (var snapshot in source.List())
        {
            if (RowExists(snapshot.Header.Id))
                continue;
            // Zstd 日志走帧视图(原样字节); 明文日志没有帧, 退回"读事件 + 同一套切帧规则"。
            var frames = source.TryReadLogFrames(snapshot.Header.Id)
                ?? SessionLogFrames.FromEvents(snapshot.Header, snapshot.InheritedEventCount,
                    ReadAllEvents(source, snapshot.Header.Id), WriteBatch.MaxPlaintextBytes, PackChunks);
            InsertLog(frames, generation: 0);
            imported += 1;
            progress?.Invoke($"imported \"{snapshot.Header.Id}\": {frames.TotalEventCount} event(s)");
        }
        Warn?.Invoke($"session database: imported {imported} session(s) from {jsonlRoot}");
        return imported;
    }

    /** 把一个会话导出成今天的日志字节(`session.jsonl.zstd`): 头帧 + 全部帧按序写出, 与 JSONL 后端同构。 */
    public string ExportTo(string jsonlRoot, SessionId id)
    {
        var row = ReadRow(id) ?? throw new SessionPersistenceNotFoundException(id);
        var frames = TryReadLogFrames(id) ?? throw new SessionPersistenceCorruptionException($"session \"{id}\": nothing to export");
        var path = JsonlLayout.LogPath(jsonlRoot, row.Header.Cwd, id, JsonlCompression.Zstd);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            frames.WriteTo(stream);
            stream.Flush(true);
        }
        SessionLogIndex.Delete(path);
        return path;
    }

    /** 收一份别的中心交来的日志: 归属换成自己, 代数按调用方给的写(交接一次 +1)。 */
    public void ImportLog(SessionLogFrames frames, long generation) => InsertLog(frames, generation);
    /** 把一份日志写成一行 `log` 加它的全部帧(一个事务, 流式插入)。 */
    private void InsertLog(SessionLogFrames frames, long generation)
    {
        var header = frames.Header;
        Store.Admin(connection =>
        {
            connection.Execute("BEGIN");
            try
            {
                connection.Execute(
                    "INSERT OR REPLACE INTO log(session_id, owner_node, generation, writable, durability_seq, frame_bytes, revision, header_frame) " +
                    "VALUES(?1, ?2, ?3, 1, ?4, 0, 0, ?5)",
                    statement =>
                    {
                        statement.BindText(1, header.Id.Value);
                        statement.BindText(2, NodeId);
                        statement.BindInt(3, generation);
                        statement.BindInt(4, frames.TotalEventCount);
                        statement.BindBlob(5, frames.HeaderFrame);
                    });
                var byteCount = InsertFrameRows(connection, header.Id.Value, generation, frames.All);
                connection.Execute("UPDATE log SET frame_bytes = ?1 WHERE session_id = ?2",
                    statement => { statement.BindInt(1, byteCount); statement.BindText(2, header.Id.Value); });
                connection.Execute("COMMIT");
            }
            catch
            {
                connection.Execute("ROLLBACK");
                throw;
            }
        });
    }

    /**
     * 逐帧插入 `frame` 行, 返回字节合计。
     *
     * 插入前按**写路径同一份明文预算**把细帧并成粗帧: 现场那批老日志是"一条事件一帧"(383MB / 104 万帧),
     * 一行一帧意味着每次打开要遍历 104 万行(实测冷开 59% 的时间在 C ABI 的 `step` 上),
     * 库里也白白多存了每帧的页开销。并帧只做"解压 + 拼接明文 + 重新压缩", 不解析事件、不改序号,
     * 因此事件内容与顺序完全不变; 之后导出得到的是一份更规整的日志, 文件分支打开同样受益。
     */
    private long InsertFrameRows(
        TursoConnection connection, string sessionId, long generation, IEnumerable<SessionLogFrames.Frame> frames)
    {
        var statement = connection.Prepare(
            "INSERT OR REPLACE INTO frame(session_id, generation, first_seq, event_count, min_replace_start, bytes) " +
            "VALUES(?1, ?2, ?3, ?4, ?5, ?6)");
        var budget = WriteBatch.MaxPlaintextBytes;
        var pending = new System.Buffers.ArrayBufferWriter<byte>(budget);
        long pendingFirstSeq = 0;
        long pendingMinReplace = long.MaxValue;
        var pendingEvents = 0;
        var byteCount = 0L;

        void Flush()
        {
            if (pendingEvents == 0)
                return;
            var content = ZstdFrames.CompressFrame(pending.WrittenSpan);
            statement.Execute(bound =>
            {
                bound.BindText(1, sessionId);
                bound.BindInt(2, generation);
                bound.BindInt(3, pendingFirstSeq);
                bound.BindInt(4, pendingEvents);
                bound.BindInt(5, pendingMinReplace);
                bound.BindBlob(6, content);
            });
            byteCount += content.Length;
            pending.ResetWrittenCount();
            pendingEvents = 0;
            pendingMinReplace = long.MaxValue;
        }

        foreach (var frame in frames)
        {
            var plain = ZstdFrames.DecompressFrame(frame.Bytes, 0);
            if (pendingEvents > 0 && pending.WrittenCount + plain.Length > budget)
                Flush();
            if (pendingEvents == 0)
                pendingFirstSeq = frame.FirstSeq;
            plain.CopyTo(pending.GetSpan(plain.Length));
            pending.Advance(plain.Length);
            pendingEvents += frame.EventCount;
            pendingMinReplace = Math.Min(pendingMinReplace, frame.MinReplaceStart);
        }
        Flush();
        return byteCount;
    }

    /** 把一段"已编号、未提交"的帧写进库: 只动 `frame` 与 `log.durability_seq`, 不碰头字段。 */
    private void AppendFrames(LogRow row, IReadOnlyList<SessionLogFrames.Frame> frames)
    {
        var eventCount = frames.Sum(frame => (long)frame.EventCount);
        var byteCount = frames.Sum(frame => (long)frame.Bytes.Length);
        Store.Admin(connection =>
        {
            connection.Execute("BEGIN");
            try
            {
                var changed = connection.Execute(
                    "UPDATE log SET durability_seq = ?1, frame_bytes = frame_bytes + ?2, revision = revision + 1 " +
                    "WHERE session_id = ?3 AND durability_seq = ?4",
                    statement =>
                    {
                        statement.BindInt(1, row.DurabilitySeq + eventCount);
                        statement.BindInt(2, byteCount);
                        statement.BindText(3, row.Header.Id.Value);
                        statement.BindInt(4, row.DurabilitySeq);
                    });
                if (changed != 1)
                    throw new SessionNumberStaleException(row.DurabilitySeq, frames[0].FirstSeq);
                InsertFrameRows(connection, row.Header.Id.Value, row.Generation, frames);
                connection.Execute("COMMIT");
            }
            catch
            {
                connection.Execute("ROLLBACK");
                throw;
            }
        });
    }

    /**
     * 重放上次写不进去而另存的帧（`<db>.pending-*.log`）。
     *
     * 收的条件是"这段帧正好接在库里的条数后面": 对不上说明会话已经被推进或删掉, 文件原样留着并 WARN,
     * 人工用 `tursodb` / 导出路径接手 —— 自动重放只做它能确定的那一部分。
     */
    private void ReplayPendingEscrow()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (directory is null || !Directory.Exists(directory))
            return;
        foreach (var path in Directory.EnumerateFiles(directory, $"{Path.GetFileName(_dbPath)}.pending-*.log"))
            ReplayEscrowFile(path);
    }

    private void ReplayEscrowFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var scan = ZstdFrames.Scan(bytes);
            if (scan.Frames.Count < 2 || scan.Damaged.Count > 0 || scan.TornStart is not null)
            {
                Warn?.Invoke($"session escrow \"{path}\" is unreadable; left for manual recovery");
                return;
            }
            var headerFrame = bytes[scan.Frames[0].Start..scan.Frames[0].End];
            var row = ReadRow(SessionRowCodec.DecodeHeaderFrame(headerFrame).Meta.Id);
            var frames = new List<SessionLogFrames.Frame>();
            foreach (var (start, end) in SessionLogFrameCodec.EventFrames(bytes))
            {
                var (firstSeq, eventCount) = SessionLogFrameCodec.Measure(bytes, (start, end));
                frames.Add(new SessionLogFrames.Frame(firstSeq, eventCount, bytes[start..end]));
            }
            if (row is null || frames.Count == 0)
            {
                Warn?.Invoke($"session escrow \"{path}\": its session is gone; left for manual recovery");
                return;
            }
            if (frames[0].FirstSeq != row.DurabilitySeq)
            {
                Warn?.Invoke($"session escrow \"{path}\" starts at #{frames[0].FirstSeq} but the log holds {row.DurabilitySeq} event(s); "
                    + "left for manual recovery");
                return;
            }
            AppendFrames(row, frames);
            File.Delete(path);
            Warn?.Invoke($"session \"{row.Header.Id}\": replayed {frames.Sum(frame => frame.EventCount)} escrowed event(s) from {path}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
            or SessionPersistenceException)
        {
            Warn?.Invoke($"session escrow \"{path}\" could not be replayed ({error.GetType().Name}: {error.Message}); left in place");
        }
    }

    /** 明文日志没有帧视图时退回事件读取(切帧仍走共享的 `SessionLogFrames.FromEvents`)。 */
    private static IReadOnlyList<SessionEvent> ReadAllEvents(JsonlSessionPersistence source, SessionId id)
    {
        using var handle = source.Open(id, SessionAccess.Read);
        return handle.Read();
    }
}




