using BenchmarkDotNet.Attributes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Benchmarks;

/**
 * 写盘路径: 同一批事件用不同批次预算落盘, 量"写完的墙钟"和"留下的日志体积"。
 *
 * 这是帧粒度调参的主战场 —— 帧越粗, 索引越小、打开越快, 但跨事件压缩比会掉; 帧越细则相反。
 * 所以这里同时报 LogBytes 与 Frames: 只有两个数一起看, 才知道某个预算是在拿什么换什么。
 *
 * 基准体每轮新建一个空目录并写完整份日志, 所以写盘与 fsync 都在被测量范围内 (fsync 是真实成本)。
 */
[MemoryDiagnoser]
public class SessionWriteBenchmarks
{
    private string _root = "";
    private List<SessionEvent> _events = [];
    private SessionId _id;

    [Params(SessionShape.ToolHeavy, SessionShape.LargeToolOutput)]
    public SessionShape Shape { get; set; }

    /** 明文字节预算: 决定一帧装多少内容。条数上限固定足够大, 让字节预算真正起主导作用。 */
    [Params(256 * 1024, 1024 * 1024, 4 * 1024 * 1024)]
    public int MaxPlaintextBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _id = SessionFactory.NewSessionId();
        _events = SessionFactory.Build(Shape, 50_000);
    }

    [IterationSetup]
    public void NewRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dsh-bench-write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [IterationCleanup]
    public void DropRoot()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Benchmark]
    public long Write()
    {
        var batch = new JsonlWriteBatch(1_000_000, TimeSpan.FromMilliseconds(100))
        {
            MaxPlaintextBytes = MaxPlaintextBytes,
        };
        using var store = new JsonlSessionPersistence(_root, writeBatch: batch);
        using var handle = store.Create(SessionFactory.Header(_id));
        // 逐条追加: 与 Plugin 事件落盘的真实路径一致, 批量追加会绕过攒批逻辑。
        foreach (var sessionEvent in _events)
            handle.Append([sessionEvent]);
        handle.Flush();
        handle.Close();

        var log = Directory.EnumerateFiles(_root, "session.jsonl*", SearchOption.AllDirectories)
            .Single(path => !path.EndsWith(SessionLogIndex.Extension, StringComparison.Ordinal));
        return new FileInfo(log).Length;
    }
}