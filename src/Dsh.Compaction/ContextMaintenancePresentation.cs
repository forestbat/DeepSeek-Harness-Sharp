using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Compaction;

/**
 * 把压缩事件翻译成交互层的中立视图(见 IContextMaintenancePresenter)。
 *
 * 这样 TUI/GUI 只依赖 Dsh.Interaction 就能渲染压缩进度, 不必引用本程序集;
 * 本插件没装时界面那边的取值为 null, 压缩进度自然不显示, 其余功能不受影响。
 */
public sealed class CompactionMaintenancePresenter(Context ctx)
    : Service(ctx, IContextMaintenancePresenter.ServiceName), IContextMaintenancePresenter
{
    public static CompactionMaintenancePresenter Register(Context ctx) => new(ctx);

    public ContextMaintenanceEvent? Describe(SessionEvent sessionEvent) => sessionEvent.Data switch
    {
        CompactionStartPayload => new ContextMaintenanceEvent.Started(),
        CompactionPrunePayload prune => new ContextMaintenanceEvent.Pruned(prune.ShadowedSeqs.Count, prune.ShadowedTokenCount),
        CompactionSummaryPayload summary => new ContextMaintenanceEvent.Summarized(
            summary.ShadowedSeqs.Count,
            summary.ShadowedTokenCount,
            MessageText.Flatten(summary.Summary)),
        CompactionEndPayload { Error: { Length: > 0 } failure } => new ContextMaintenanceEvent.Failed(failure),
        CompactionEndPayload => new ContextMaintenanceEvent.Finished(),
        _ => null,
    };

    public bool IsCheckpointMessage(MessageSource source) => CompactionCheckpoint.IsCheckpointSource(source);
}
