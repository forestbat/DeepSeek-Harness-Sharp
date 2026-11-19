using Dsh.Llm;

namespace Dsh.Interaction;

/**
 * 状态行统计: 上下文占用、轮/步、首 token 延迟、tok/s、缓存命中、累计 in/out、LLM 用时。
 *
 * TUI 与 GUI 用同一份口径: GUI 的状态栏早就把这些都显示出来了, TUI 只显示上下文与模型,
 * 长会话里看不出"请求是不是慢、缓存有没有命中", 排障时没抓手。
 */
public sealed class SessionStatistics
{
    private const double MinimumMeasurableSeconds = 0.05;
    private const string Empty = "-";

    private readonly ContextUsage _context = new();
    private long _requestStartedAt;
    private bool _measuring;
    private bool _firstTokenSeen;
    private long _inputTokens;
    private long _outputTokens;
    private long _cacheReadTokens;

    public string ContextText { get; private set; } = Empty;

    public string Rounds { get; private set; } = Empty;

    public string FirstToken { get; private set; } = Empty;

    public string TokensPerSecond { get; private set; } = Empty;

    public string Latency { get; private set; } = Empty;

    public string Cache { get; private set; } = Empty;

    public string Totals { get; private set; } = Empty;

    /** 状态行片段: 跳过还没有值的项, 免得满行 "-"。 */
    public IReadOnlyList<string> Segments()
    {
        var segments = new List<string>(7);
        foreach (var segment in new[] { ContextText, Rounds, FirstToken, TokensPerSecond, Latency, Cache, Totals })
        {
            if (segment.Length > 0 && segment != Empty)
                segments.Add(segment);
        }
        return segments;
    }

    public void ObserveRequestContext(int? contextWindow)
    {
        _context.ObserveRequest(contextWindow);
        ContextText = _context.Label;
    }

    public void ObserveTurn(int turn, int step)
        => Rounds = step > 0 ? $"轮 {turn} · 步 {step}" : $"轮 {turn}";

    public void RequestStarted()
    {
        _firstTokenSeen = false;
        _measuring = true;
        _requestStartedAt = Environment.TickCount64;
    }

    public void FirstTokenArrived()
    {
        if (_firstTokenSeen || !_measuring)
            return;
        _firstTokenSeen = true;
        FirstToken = $"首 token {Elapsed.TotalSeconds:0.0}s";
    }

    public void RequestFinished()
    {
        if (!_measuring)
            return;
        var elapsed = Elapsed;
        _measuring = false;
        if (elapsed.TotalSeconds >= MinimumMeasurableSeconds)
            Latency = $"LLM {elapsed.TotalSeconds:0.0}s";
    }

    public void ObserveUsage(TokenUsage usage)
    {
        if (usage.InputTokens + usage.OutputTokens <= 0)
            return;
        // 上下文占用只认最近一次请求的提示词, 与累计统计是两回事。
        _context.ObserveUsage(usage);
        ContextText = _context.Label;
        _inputTokens += (long)usage.InputTokens;
        _outputTokens += (long)usage.OutputTokens;
        _cacheReadTokens += (long)(usage.CacheReadTokens ?? 0);
        Totals = $"in {Compact(_inputTokens)} / out {Compact(_outputTokens)}";
        Cache = _inputTokens > 0 ? $"缓存命中 {_cacheReadTokens / _inputTokens * 100:0}%" : Empty;
        var seconds = Elapsed.TotalSeconds;
        // 重放历史会话时事件瞬间连发, 这种耗时算出的 tok/s 没有意义。
        if (usage.OutputTokens > 0 && seconds >= MinimumMeasurableSeconds)
            TokensPerSecond = $"{usage.OutputTokens / seconds:0.0} tok/s";
    }

    private TimeSpan Elapsed
        => _measuring ? TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - _requestStartedAt)) : TimeSpan.Zero;

    private static string Compact(long tokens)
        => tokens switch
        {
            >= 1_000_000 => $"{tokens / 1_000_000.0:0.0}M",
            >= 1_000 => $"{tokens / 1_000.0:0.0}k",
            _ => tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
}
