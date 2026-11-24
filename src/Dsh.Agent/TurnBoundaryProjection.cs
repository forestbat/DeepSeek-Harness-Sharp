using Dsh.Core;

namespace Dsh.Agent;

public sealed record TurnBoundaryStepMarker(string Kind, long Seq);

public sealed record TurnBoundaryProjection
{
    public long? OpenTurnStartSeq { get; init; }
    public long? LastStepStartSeq { get; init; }
    public TurnBoundaryStepMarker? LastStepBoundary { get; init; }
    public int LastTurn { get; init; }
}

public static class TurnBoundaryProjectionDefinition
{
    public const string Key = "turnBoundary";
    public const int StateVersion = 2;

    public static readonly SessionProjectionDefinition<TurnBoundaryProjection> Instance = new(
        Key,
        StateVersion,
        (_, _) => new TurnBoundaryProjection(),
        Apply);

    private static TurnBoundaryProjection Apply(TurnBoundaryProjection state, SessionEvent sessionEvent)
        => sessionEvent.Data switch
        {
            TurnStartPayload turnStart => state with { OpenTurnStartSeq = sessionEvent.Seq, LastTurn = turnStart.Turn },
            TurnEndPayload => state with { OpenTurnStartSeq = null },
            // 回退只追加一条事件、不缩短日志, 所以"被收掉的回合没有 TurnEnd"这件事必须靠它自己说出来:
            // 否则折叠结果里始终有一个开着的回合, 界面重放就永远停在"正在运行"。
            SessionRevertPayload revert => state.OpenTurnStartSeq >= revert.TargetSeq
                ? state with { OpenTurnStartSeq = null }
                : state,
            StepStartPayload => state with
            {
                LastStepStartSeq = sessionEvent.Seq,
                LastStepBoundary = new TurnBoundaryStepMarker("start", sessionEvent.Seq),
            },
            StepEndPayload => state with { LastStepBoundary = new TurnBoundaryStepMarker("end", sessionEvent.Seq) },
            _ => state,
        };
}
