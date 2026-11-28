using System.Runtime.InteropServices;

namespace Dsh.Persistence.Database;

/** 提交道的 IO 驱动: 同步(默认) 或 `io_uring` 异步(内核 ≥5.19, 需显式启用)。 */
public enum TursoIoDriver
{
    Sync,
    IoUring,
}

/**
 * 本地库: 一个 home 一个文件, 进程内独占打开。
 *
 * 连接由调用方各自持有(Turso 的连接不能并发使用): 提交道每道一个, 读走一个小池子。
 */
internal sealed class TursoDatabase : IDisposable
{
    private readonly IntPtr _handle;
    private readonly List<IntPtr> _configStrings;
    private bool _disposed;

    private TursoDatabase(IntPtr handle, List<IntPtr> configStrings, TursoIoDriver driver)
    {
        _handle = handle;
        _configStrings = configStrings;
        Driver = driver;
    }

    public TursoIoDriver Driver { get; }

    public string EngineVersion => TursoNative.EngineVersion();

    /**
     * 打开库。请求的驱动不可用时退回同步并记 WARN: io_uring 需要内核 ≥5.19,
     * 5.15 上会在注册缓冲区这一步报 `EINVAL`, 那不是数据问题。
     */
    public static TursoDatabase Open(string path, TursoIoDriver driver, Action<string>? warn)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (driver == TursoIoDriver.IoUring)
        {
            try
            {
                return OpenCore(path, TursoIoDriver.IoUring);
            }
            catch (SessionDatabaseException error)
            {
                warn?.Invoke($"session database: the io_uring driver is unavailable ({error.Message}); using the synchronous driver");
            }
        }
        return OpenCore(path, TursoIoDriver.Sync);
    }

    private static TursoDatabase OpenCore(string path, TursoIoDriver driver)
    {
        var strings = new List<IntPtr> { Marshal.StringToCoTaskMemUTF8(path) };
        if (driver == TursoIoDriver.IoUring)
            strings.Add(Marshal.StringToCoTaskMemUTF8("io_uring"));
        var config = new TursoDatabaseConfig
        {
            AsyncIo = driver == TursoIoDriver.IoUring ? 1UL : 0UL,
            Path = strings[0],
            Vfs = driver == TursoIoDriver.IoUring ? strings[1] : IntPtr.Zero,
        };
        var status = TursoNative.DatabaseNew(ref config, out var database, out var error);
        if (status != TursoStatus.Ok)
        {
            foreach (var text in strings)
                Marshal.FreeCoTaskMem(text);
            throw new SessionDatabaseException(status, TursoNative.TakeError(error));
        }
        status = TursoNative.DatabaseOpen(database, out error);
        if (status != TursoStatus.Ok)
        {
            TursoNative.DatabaseDeinit(database);
            foreach (var text in strings)
                Marshal.FreeCoTaskMem(text);
            throw new SessionDatabaseException(status, TursoNative.TakeError(error));
        }
        return new TursoDatabase(database, strings, driver);
    }

    public TursoConnection Connect(long busyTimeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var status = TursoNative.DatabaseConnect(_handle, out var connection, out var error);
        if (status != TursoStatus.Ok)
            throw new SessionDatabaseException(status, TursoNative.TakeError(error));
        TursoNative.ConnectionSetBusyTimeout(connection, busyTimeoutMs);
        return new TursoConnection(connection);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        TursoNative.DatabaseDeinit(_handle);
        foreach (var text in _configStrings)
            Marshal.FreeCoTaskMem(text);
    }
}

/**
 * 一条连接: 所有调用串行化(引擎要求连接独占使用), 并缓存已编译的语句。
 *
 * `turso_statement_reset` 会**清空已绑定的参数**, 所以每次执行都是 reset → bind → step;
 * 缓存的语句因此可以反复用, 但绝不能"改一个参数就再 step 一次"。
 */
internal sealed class TursoConnection : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TursoStatement> _statements = new(StringComparer.Ordinal);
    private readonly IntPtr _handle;
    private bool _disposed;

    public TursoConnection(IntPtr handle) => _handle = handle;

    /** 事务用: 期间独占这条连接(可重入, 内部方法照样能进)。 */
    public Scope Exclusive()
    {
        Monitor.Enter(_gate);
        return new Scope(_gate);
    }

    public readonly struct Scope(object gate) : IDisposable
    {
        public void Dispose() => Monitor.Exit(gate);
    }

    public TursoStatement Prepare(string sql)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_statements.TryGetValue(sql, out var cached))
                return cached;
            var status = TursoNative.Prepare(_handle, sql, out var statement, out var error);
            if (status != TursoStatus.Ok)
                throw new SessionDatabaseException(status, TursoNative.TakeError(error));
            var prepared = new TursoStatement(statement, sql);
            _statements[sql] = prepared;
            return prepared;
        }
    }

    /** 执行一条不返回行的语句, 返回受影响行数。 */
    public long Execute(string sql, Action<TursoStatement>? bind = null)
    {
        lock (_gate)
        {
            var statement = Prepare(sql);
            return statement.Execute(bind);
        }
    }

    /** 执行一条语句并逐行回调(回调返回 false 可提前停止)。 */
    public void Query(string sql, Action<TursoStatement>? bind, Action<TursoStatement> onRow)
    {
        lock (_gate)
        {
            Prepare(sql).Query(bind, onRow);
        }
    }

    public long Scalar(string sql, Action<TursoStatement>? bind = null)
    {
        var value = -1L;
        Query(sql, bind, statement => value = statement.Int(0));
        return value;
    }

    public string? ScalarText(string sql, Action<TursoStatement>? bind = null)
    {
        string? value = null;
        Query(sql, bind, statement => value = statement.Kind(0) == TursoValueType.Text ? statement.Text(0) : null);
        return value;
    }

    public void ExecuteScript(string script)
    {
        foreach (var statement in SplitStatements(script))
        {
            if (statement.Length > 0)
                Execute(statement);
        }
    }

    /** 脚本按分号切: DDL 里没有字符串内的分号, 切错会立刻在 prepare 上暴露。 */
    private static IEnumerable<string> SplitStatements(string script)
        => script.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /** 备份与整库搬运的唯一手段: MVCC 下直接可用, 副本可独立打开。 */
    public void VacuumInto(string target)
    {
        lock (_gate)
        {
            // 引擎要求 VACUUM 前没有未结束的语句, 缓存里的语句先全部结掉。
            foreach (var statement in _statements.Values)
                statement.Finish();
            _statements.Clear();
            Execute($"VACUUM INTO '{target.Replace("'", "''")}'");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var statement in _statements.Values)
                statement.Finish();
            _statements.Clear();
            TursoNative.ConnectionClose(_handle, out var error);
            if (error != IntPtr.Zero)
                TursoNative.TakeError(error);
            TursoNative.ConnectionDeinit(_handle);
        }
    }
}

/** 一条已编译的语句: 绑定、步进、读列。 */
internal sealed class TursoStatement(IntPtr handle, string sql) : IDisposable
{
    private bool _finished;

    public long Execute(Action<TursoStatement>? bind)
    {
        Reset();
        bind?.Invoke(this);
        while (true)
        {
            var status = Step();
            if (status is TursoStatus.Done or TursoStatus.Ok)
                return TursoNative.RowsChanged(handle);
            if (status != TursoStatus.Row)
                throw new InvalidOperationException($"statement produced an unexpected row: {sql}");
        }
    }

    public void Query(Action<TursoStatement>? bind, Action<TursoStatement> onRow)
    {
        Reset();
        bind?.Invoke(this);
        while (Step() == TursoStatus.Row)
            onRow(this);
    }

    public void Reset()
    {
        var status = TursoNative.Reset(handle, out var error);
        if (status != TursoStatus.Ok && status != TursoStatus.Done)
            throw new SessionDatabaseException(status, TursoNative.TakeError(error));
    }

    /** 步进到有结果为止: 异步驱动下 `TURSO_IO` 由这里接手轮询。 */
    public TursoStatus Step()
    {
        while (true)
        {
            var status = TursoNative.Step(handle, out var error);
            if (status == TursoStatus.Io)
            {
                var io = TursoNative.RunIo(handle, out error);
                if (io is TursoStatus.Ok or TursoStatus.Io)
                    continue;
                throw new SessionDatabaseException(io, TursoNative.TakeError(error));
            }
            if (status is TursoStatus.Row or TursoStatus.Done or TursoStatus.Ok)
                return status;
            throw new SessionDatabaseException(status, TursoNative.TakeError(error));
        }
    }

    public void BindInt(int position, long value)
    {
        var status = TursoNative.BindInt(handle, (UIntPtr)position, value);
        if (status != TursoStatus.Ok)
            throw new SessionDatabaseException(status, $"bind #{position} of {sql}");
    }

    public void BindDouble(int position, double value)
    {
        var status = TursoNative.BindDouble(handle, (UIntPtr)position, value);
        if (status != TursoStatus.Ok)
            throw new SessionDatabaseException(status, $"bind #{position} of {sql}");
    }

    public void BindNull(int position)
    {
        var status = TursoNative.BindNull(handle, (UIntPtr)position);
        if (status != TursoStatus.Ok)
            throw new SessionDatabaseException(status, $"bind #{position} of {sql}");
    }

    public void BindText(int position, string? value)
    {
        if (value is null)
        {
            BindNull(position);
            return;
        }
        var status = TursoNative.BindText(handle, (UIntPtr)position, value, (UIntPtr)System.Text.Encoding.UTF8.GetByteCount(value));
        if (status != TursoStatus.Ok)
            throw new SessionDatabaseException(status, $"bind #{position} of {sql}");
    }

    public void BindBlob(int position, byte[] value)
    {
        var status = TursoNative.BindBlob(handle, (UIntPtr)position, value, (UIntPtr)value.Length);
        if (status != TursoStatus.Ok)
            throw new SessionDatabaseException(status, $"bind #{position} of {sql}");
    }

    public TursoValueType Kind(int index) => TursoNative.RowValueKind(handle, (UIntPtr)index);

    public long Int(int index) => TursoNative.RowValueInt(handle, (UIntPtr)index);

    public double Double(int index) => TursoNative.RowValueDouble(handle, (UIntPtr)index);

    public bool IsNull(int index) => Kind(index) == TursoValueType.Null;

    public string? Text(int index)
    {
        var count = TursoNative.RowValueBytesCount(handle, (UIntPtr)index);
        if (count < 0)
            return null;
        var pointer = TursoNative.RowValueBytesPtr(handle, (UIntPtr)index);
        return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer, checked((int)count));
    }

    /**
     * 列的值作为**只读区间**交出: 直接指向引擎的结果缓冲, 不复制。
     * 区间只在这一次行回调内有效(下一次 step/reset/finalize 就失效) —— 需要在回调里解码/消费。
     */
    public unsafe ReadOnlySpan<byte> BytesSpan(int index)
    {
        var count = TursoNative.RowValueBytesCount(handle, (UIntPtr)index);
        if (count <= 0)
            return ReadOnlySpan<byte>.Empty;
        var pointer = TursoNative.RowValueBytesPtr(handle, (UIntPtr)index);
        return pointer == IntPtr.Zero ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>((void*)pointer, checked((int)count));
    }

    public byte[]? Bytes(int index)
    {
        var count = TursoNative.RowValueBytesCount(handle, (UIntPtr)index);
        if (count < 0)
            return null;
        var pointer = TursoNative.RowValueBytesPtr(handle, (UIntPtr)index);
        if (pointer == IntPtr.Zero || count == 0)
            return [];
        var buffer = new byte[count];
        Marshal.Copy(pointer, buffer, 0, buffer.Length);
        return buffer;
    }

    public void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        TursoNative.Finalize(handle, out var error);
        if (error != IntPtr.Zero)
            TursoNative.TakeError(error);
    }

    public void Dispose() => Finish();
}



