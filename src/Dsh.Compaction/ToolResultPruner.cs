using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

public sealed record ToolResultPruneConfig
{
    public int? ThresholdChars { get; init; }
    public int? HeadChars { get; init; }
    public int? TailChars { get; init; }
}

public sealed record ResolvedPruneConfig(int ThresholdChars, int HeadChars, int TailChars);

public sealed record PrunedEntry(long OriginalSeq, long ReplacementSeq, ToolCallId CallId, int CharsBefore, int CharsAfter);

public sealed record PruneResult(IReadOnlyList<PrunedEntry> Pruned, int CharsRemoved);

public sealed class ToolResultPruner : Service, IDisposable
{
    public const string ServiceName = "toolResultPruner";
    public const string PruneMarker = "\n\n[... tool result middle pruned ...]\n\n";

    public const int DefaultThresholdChars = 8192;
    public const int DefaultHeadChars = 4096;
    public const int DefaultTailChars = 1024;

    private readonly TokenMeter _meter;

    public ResolvedPruneConfig Config { get; }

    static ToolResultPruner() => CompactionEventTypes.EnsureRegistered();

    public ToolResultPruner(Context ctx, ToolResultPruneConfig? config = null) : base(ctx, ServiceName)
    {
        _meter = ctx.Get<TokenMeter>(TokenMeter.ServiceName)
            ?? throw new InvalidOperationException("tool-result pruning requires the tokenMeter service");
        Config = ResolveConfig(config);
    }

    public static ToolResultPruner Register(Context ctx, ToolResultPruneConfig? config = null) => new(ctx, config);

    public void Dispose()
    {
    }

    public static ResolvedPruneConfig ResolveConfig(ToolResultPruneConfig? config = null)
    {
        var resolved = new ResolvedPruneConfig(
            config?.ThresholdChars ?? DefaultThresholdChars,
            config?.HeadChars ?? DefaultHeadChars,
            config?.TailChars ?? DefaultTailChars);
        if (resolved.ThresholdChars <= 0)
            throw new ArgumentException($"ToolResultPruneConfig: thresholdChars ({resolved.ThresholdChars}) must be a positive integer");
        if (resolved.HeadChars < 0)
            throw new ArgumentException($"ToolResultPruneConfig: headChars ({resolved.HeadChars}) must be a non-negative integer");
        if (resolved.TailChars < 0)
            throw new ArgumentException($"ToolResultPruneConfig: tailChars ({resolved.TailChars}) must be a non-negative integer");
        var emittedChars = resolved.HeadChars + CodePointLength(PruneMarker) + resolved.TailChars;
        if (emittedChars > resolved.ThresholdChars)
            throw new ArgumentException(
                $"ToolResultPruneConfig: headChars + marker + tailChars ({emittedChars}) must be at most thresholdChars ({resolved.ThresholdChars})");
        return resolved;
    }

    public static int CodePointLength(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
            count++;
        return count;
    }

    public int MeasureContent(IReadOnlyList<ContentBlock> blocks)
    {
        var chars = 0;
        foreach (var block in blocks)
        {
            if (block is TextBlock text)
                chars += CodePointLength(text.Text);
        }
        return chars;
    }

    public IReadOnlyList<ContentBlock>? PruneContent(IReadOnlyList<ContentBlock> blocks)
    {
        var totalChars = MeasureContent(blocks);
        if (totalChars <= Config.ThresholdChars)
            return null;

        var removedStart = Config.HeadChars;
        var removedEnd = totalChars - Config.TailChars;
        var pruned = new List<ContentBlock>();
        var consumed = 0;
        var markerInserted = false;

        foreach (var block in blocks)
        {
            if (block is not TextBlock textBlock)
            {
                pruned.Add(block);
                continue;
            }

            var offsets = CodePointOffsets(textBlock.Text);
            var points = offsets.Length;
            var blockStart = consumed;
            var blockEnd = blockStart + points;
            var headEnd = Math.Min(points, Math.Max(0, removedStart - blockStart));
            var tailStart = Math.Min(points, Math.Max(0, removedEnd - blockStart));
            var intersectsRemoved = blockStart < removedEnd && blockEnd > removedStart;
            var marker = intersectsRemoved && !markerInserted ? PruneMarker : "";
            if (marker.Length > 0)
                markerInserted = true;
            var head = headEnd < points ? textBlock.Text[..offsets[headEnd]] : textBlock.Text;
            var tail = tailStart < points ? textBlock.Text[offsets[tailStart]..] : "";
            var text = string.Concat(head, marker, tail);
            if (text.Length > 0)
                pruned.Add(textBlock with { Text = text });
            consumed = blockEnd;
        }

        if (!markerInserted)
            throw new InvalidOperationException("tool-result prune: failed to locate the removed text span");
        var charsAfter = MeasureContent(pruned);
        if (charsAfter > Config.ThresholdChars || charsAfter >= totalChars)
            throw new InvalidOperationException("tool-result prune: replacement must be smaller and within threshold");
        return pruned;
    }

    public PruneResult PruneSession(Session session)
    {
        var candidates = new List<(long Seq, ToolResultPayload Payload)>();
        foreach (var seq in session.SurfaceManager.Nodes.ToList())
        {
            if (session.EventAt(seq)?.Data is ToolResultPayload payload)
                candidates.Add((seq, payload));
        }

        var pruned = new List<PrunedEntry>();
        var charsRemoved = 0;
        foreach (var (seq, payload) in candidates)
        {
            var result = payload.Message.Block;
            var content = PruneContent(result.Content);
            if (content is null)
                continue;
            var charsBefore = MeasureContent(result.Content);
            var charsAfter = MeasureContent(content);
            var message = payload.Message with { Content = [result with { Content = content }] };
            session.Append(new CompactionPrunePayload(new ShadowedRange(seq, seq), [seq], _meter.EstimateMessage(payload.Message)));
            var replacement = session.Append(
                new ToolResultPayload(payload.Turn, payload.Step, message, payload.Error, payload.Meta),
                new SurfaceOp.Replace(seq, seq),
                [seq]);
            pruned.Add(new PrunedEntry(seq, replacement.Seq, payload.Message.ToolSource.CallId, charsBefore, charsAfter));
            charsRemoved += charsBefore - charsAfter;
        }
        return new PruneResult(pruned, charsRemoved);
    }

    private static int[] CodePointOffsets(string text)
    {
        var offsets = new List<int>();
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            offsets.Add(index);
            index += rune.Utf16SequenceLength;
        }
        return [.. offsets];
    }
}
