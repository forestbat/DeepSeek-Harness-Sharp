using BenchmarkDotNet.Attributes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Benchmarks;

/**
 * 会话载入的端到端代价。
 *
 * 每个参数组合在 GlobalSetup 里先落一份日志到临时目录, 基准体只量"打开 + 读"这一侧,
 * 于是写盘开销不会混进被测量的数字。
 *
 * 量的是三件事, 分开报:
 *  - OpenRead: 冷缓存 Open(Read) + Read(), 即"全量展开一份会话"的真实代价。
 *  - OpenWindow: TryOpenWindow(tail), 即窗口化打开这条主路径。
 *  - OpenWrite: Open(Write), 即恢复会话后立刻要写时的代价(它要拿到落盘位置)。
 */
[MemoryDiagnoser]
public class SessionLoadBenchmarks
{
    private string _root = "";
    private SessionId _id;

    [Params(SessionShape.Ordinary, SessionShape.ToolHeavy, SessionShape.Compacted, SessionShape.LargeToolOutput)]
    public SessionShape Shape { get; set; }

    [Params(10_000, 100_000)]
    public int EventCount { get; set; }

    /** 压缩与不压缩的载入代价必须分开报: zstd 换体积, 代价落在解压上。 */
    [Params(JsonlCompression.Zstd, JsonlCompression.None)]
    public JsonlCompression Compression { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dsh-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _id = SessionFactory.NewSessionId();
        var header = SessionFactory.Header(_id);
        var events = SessionFactory.Build(Shape, EventCount);
        using var handle = new JsonlSessionPersistence(_root, compression: Compression).Create(header);
        handle.Append(events);
        handle.Flush();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Benchmark]
    public int OpenRead()
    {
        using var store = new JsonlSessionPersistence(_root);
        using var handle = store.Open(_id, SessionAccess.Read);
        return handle.Read().Count;
    }

    [Benchmark]
    public int OpenWindow()
    {
        using var store = new JsonlSessionPersistence(_root);
        var window = store.TryOpenWindow(_id, 4_096);
        return window?.Resident.Count ?? 0;
    }

    [Benchmark]
    public long OpenWrite()
    {
        using var store = new JsonlSessionPersistence(_root);
        using var handle = store.Open(_id, SessionAccess.Write);
        return handle.Count;
    }
}
