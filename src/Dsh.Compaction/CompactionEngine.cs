using Cordis;
using Dsh.Core;

namespace Dsh.Compaction;

public abstract class CompactionEngine : Service
{
    public const string ServiceName = "compaction";

    protected CompactionEngine(Context ctx) : base(ctx, ServiceName)
    {
    }

    public abstract Task<CompactionResult?> CompactIfNeeded(IAgent agent, CompactionTrigger trigger, CancellationToken signal);

    public abstract Task<CompactionResult?> CompactNow(IAgent agent, CancellationToken signal, string? sourceCommandId = null);

    public abstract Task<CompactionResult> CompactRegion(long start, long end, IAgent agent, CancellationToken signal = default);
}
