using System.Runtime.InteropServices;

namespace Dsh.Persistence.Database;

/** `turso_status_code_t`: 引擎返回的每个调用结果。 */
internal enum TursoStatus : uint
{
    Ok = 0,
    Done = 1,
    Row = 2,
    Io = 3,
    Busy = 4,
    Interrupt = 5,
    BusySnapshot = 6,
    Error = 127,
    Misuse = 128,
    Constraint = 129,
    ReadOnly = 130,
    DatabaseFull = 131,
    NotADatabase = 132,
    Corrupt = 133,
    IoError = 134,
}

/** `turso_type_t`: 结果列的值类型。 */
internal enum TursoValueType
{
    Unknown = 0,
    Integer = 1,
    Real = 2,
    Text = 3,
    Blob = 4,
    Null = 5,
}

/**
 * 引擎报错。
 *
 * 写冲突只能按**消息文本**判定: `SQLITE_ERROR` 恒为错误码 127(1), `Write-write conflict`
 * 与 `database is locked` 都落在同一个码上(`plans/会话持久化-数据库后端方案-2026-11-27.md` §8.2)。
 */
internal sealed class SessionDatabaseException(TursoStatus status, string message) : Exception(message)
{
    public TursoStatus Status { get; } = status;

    /** 这一次写不进去, 下次再试(锁/忙/盘满/IO): 对应 `SessionWriteBusyException`。 */
    public bool IsTransient => Status is TursoStatus.Busy or TursoStatus.BusySnapshot or TursoStatus.IoError
        || Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("Write-write conflict", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("busy", StringComparison.OrdinalIgnoreCase);

    /** 并发写者在同一行上撞车: 重试整批即可, 不是数据坏了。 */
    public bool IsWriteConflict => Message.Contains("Write-write conflict", StringComparison.OrdinalIgnoreCase);
}

/** `turso_database_config_t`: 打开库时的全部选项(顺序与 `turso.h` 一致)。 */
[StructLayout(LayoutKind.Sequential)]
internal struct TursoDatabaseConfig
{
    public ulong AsyncIo;
    public IntPtr Path;
    public IntPtr ExperimentalFeatures;
    public IntPtr Vfs;
    public IntPtr EncryptionCipher;
    public IntPtr EncryptionHexKey;
    public IntPtr PageCodec;
    public uint OpenFlags;
}

/** `turso.h` 的 C ABI: 产品路径只经这一层碰引擎, 不引用任何托管门面。 */
internal static class TursoNative
{
    private const string Library = "turso_sdk_kit";

    static TursoNative()
    {
        // 插件按目录装载: 原生库与托管程序集并排(或在其 runtimes/<rid>/native 下), 由这里指定查找顺序。
        NativeLibrary.SetDllImportResolver(typeof(TursoNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library)
            return IntPtr.Zero;
        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }
        return IntPtr.Zero;
    }

    /** 库文件的候选路径: 程序集所在目录、进程目录、以及两处的 `runtimes/<rid>/native`。 */
    private static IEnumerable<string> Candidates()
    {
        var file = OperatingSystem.IsWindows() ? "turso_sdk_kit.dll"
            : OperatingSystem.IsMacOS() ? "libturso_sdk_kit.dylib"
            : "libturso_sdk_kit.so";
        string[] roots =
        [
            Path.GetDirectoryName(typeof(TursoNative).Assembly.Location) ?? ".",
            AppContext.BaseDirectory,
        ];
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, file);
            foreach (var rid in new[] { RuntimeInformation.RuntimeIdentifier, HostRuntimeIdentifier() })
            {
                if (!string.IsNullOrEmpty(rid))
                    yield return Path.Combine(root, "runtimes", rid, "native", file);
            }
        }
    }

    private static string HostRuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    [DllImport(Library, EntryPoint = "turso_database_new", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus DatabaseNew(ref TursoDatabaseConfig config, out IntPtr database, out IntPtr error);

    [DllImport(Library, EntryPoint = "turso_database_open", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus DatabaseOpen(IntPtr database, out IntPtr error);

    [DllImport(Library, EntryPoint = "turso_database_connect", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus DatabaseConnect(IntPtr database, out IntPtr connection, out IntPtr error);

    [DllImport(Library, EntryPoint = "turso_database_deinit", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DatabaseDeinit(IntPtr database);

    [DllImport(Library, EntryPoint = "turso_connection_close", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus ConnectionClose(IntPtr connection, out IntPtr error);

    [DllImport(Library, EntryPoint = "turso_connection_deinit", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ConnectionDeinit(IntPtr connection);

    [DllImport(Library, EntryPoint = "turso_connection_set_busy_timeout_ms", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ConnectionSetBusyTimeout(IntPtr connection, long timeoutMs);

    [DllImport(Library, EntryPoint = "turso_connection_prepare_single", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus Prepare(
        IntPtr connection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        out IntPtr statement,
        out IntPtr error);

    [DllImport(Library, EntryPoint = "turso_statement_step", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus Step(IntPtr statement, out IntPtr error);

    [DllImport(Library, EntryPoint = "turso_statement_run_io", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus RunIo(IntPtr statement, out IntPtr error);

    [DllImport(Library, EntryPoint = "turso_statement_reset", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus Reset(IntPtr statement, out IntPtr error);

    [DllImport(Library, EntryPoint = "turso_statement_finalize", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus Finalize(IntPtr statement, out IntPtr error);

    [DllImport(Library, EntryPoint = "turso_statement_n_change", CallingConvention = CallingConvention.Cdecl)]
    public static extern long RowsChanged(IntPtr statement);

    [DllImport(Library, EntryPoint = "turso_statement_column_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern long ColumnCount(IntPtr statement);

    [DllImport(Library, EntryPoint = "turso_statement_row_value_kind", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoValueType RowValueKind(IntPtr statement, UIntPtr index);

    [DllImport(Library, EntryPoint = "turso_statement_row_value_int", CallingConvention = CallingConvention.Cdecl)]
    public static extern long RowValueInt(IntPtr statement, UIntPtr index);

    [DllImport(Library, EntryPoint = "turso_statement_row_value_double", CallingConvention = CallingConvention.Cdecl)]
    public static extern double RowValueDouble(IntPtr statement, UIntPtr index);

    [DllImport(Library, EntryPoint = "turso_statement_row_value_bytes_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern long RowValueBytesCount(IntPtr statement, UIntPtr index);

    [DllImport(Library, EntryPoint = "turso_statement_row_value_bytes_ptr", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr RowValueBytesPtr(IntPtr statement, UIntPtr index);

    [DllImport(Library, EntryPoint = "turso_statement_bind_positional_int", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus BindInt(IntPtr statement, UIntPtr position, long value);

    [DllImport(Library, EntryPoint = "turso_statement_bind_positional_double", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus BindDouble(IntPtr statement, UIntPtr position, double value);

    [DllImport(Library, EntryPoint = "turso_statement_bind_positional_null", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus BindNull(IntPtr statement, UIntPtr position);

    [DllImport(Library, EntryPoint = "turso_statement_bind_positional_text", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus BindText(
        IntPtr statement,
        UIntPtr position,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        UIntPtr length);

    [DllImport(Library, EntryPoint = "turso_statement_bind_positional_blob", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoStatus BindBlob(IntPtr statement, UIntPtr position, byte[] buffer, UIntPtr length);

    [DllImport(Library, EntryPoint = "turso_str_deinit", CallingConvention = CallingConvention.Cdecl)]
    private static extern void StrDeinit(IntPtr text);

    [DllImport(Library, EntryPoint = "turso_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr VersionPtr();

    /** 取走并释放引擎给出的错误串。 */
    public static string TakeError(IntPtr error)
    {
        if (error == IntPtr.Zero)
            return "unknown error";
        try
        {
            return Marshal.PtrToStringUTF8(error) ?? "unknown error";
        }
        finally
        {
            StrDeinit(error);
        }
    }

    /** 引擎版本(引擎自身的 semver, 与托管包版本无关): 只用于启动日志与自检报告。 */
    public static string EngineVersion()
    {
        var ptr = VersionPtr();
        return ptr == IntPtr.Zero ? "unknown" : Marshal.PtrToStringUTF8(ptr) ?? "unknown";
    }
}
