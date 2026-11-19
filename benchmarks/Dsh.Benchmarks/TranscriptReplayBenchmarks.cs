using BenchmarkDotNet.Attributes;
using Dsh.Core;
using Dsh.Tui;

namespace Dsh.Benchmarks;

/**
 * 转录重放: 把一份会话的事件逐条喂给 TranscriptRenderer, 量"打开会话到能画出第一屏"里
 * 除了载入之外的那一段。
 *
 * 这是 GUI/TUI 打开长会话时仅次于读盘的第二大项(现场测得 VM 构造 + 首帧排版约 1.7 s),
 * 所以它值得一个稳定的数字, 而不是靠每次临时探针估。
 *
 * 每次迭代都新建一个渲染器: 复用一个会把上一次的折叠与行缓存算进去, 量出来的不是"从头重放"。
 */
[MemoryDiagnoser]
public class TranscriptReplayBenchmarks
{
    private List<SessionEvent> _events = [];

    [Params(SessionShape.Ordinary, SessionShape.ToolHeavy, SessionShape.Compacted)]
    public SessionShape Shape { get; set; }

    [Params(10_000, 100_000)]
    public int EventCount { get; set; }

    [GlobalSetup]
    public void Setup() => _events = SessionFactory.Build(Shape, EventCount);

    [Benchmark]
    public int Replay()
    {
        var renderer = new TranscriptRenderer();
        foreach (var sessionEvent in _events)
            renderer.AppendSessionEvent(sessionEvent, replay: true);
        return renderer.CompletedLines.Count;
    }
}