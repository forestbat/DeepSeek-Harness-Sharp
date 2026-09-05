namespace Dsh.Core;

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
            StepStartPayload => state with
            {
                LastStepStartSeq = sessionEvent.Seq,
                LastStepBoundary = new TurnBoundaryStepMarker("start", sessionEvent.Seq),
            },
            StepEndPayload => state with { LastStepBoundary = new TurnBoundaryStepMarker("end", sessionEvent.Seq) },
            _ => state,
        };
}
