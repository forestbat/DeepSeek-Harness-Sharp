using Dsh.Core;

namespace Dsh.Subagent;

public sealed record ConsumedWork(SessionEvent? End, bool DroppedUnrun);

public static class ConsumedWorkFold
{
    private static bool AccountsForClaim(TurnEndReason reason) => reason switch
    {
        TurnEndReason.Completed => false,
        _ => true,
    };

    public static ConsumedWork Fold(IReadOnlyList<SessionEvent> events)
    {
        var stepped = new HashSet<int>();
        var claimed = new HashSet<int>();
        int? open = null;
        SessionEvent? end = null;
        var droppedUnrun = false;
        foreach (var sessionEvent in events)
        {
            switch (sessionEvent.Data)
            {
                case TurnStartPayload turnStart:
                    open = turnStart.Turn;
                    break;
                case StepStartPayload stepStart:
                    stepped.Add(stepStart.Turn);
                    break;
                case InboxSplicePayload splice:
                {
                    if (splice.RemovedCount is null)
                        break;
                    if (splice.Outcome == "canceled")
                        droppedUnrun |= splice.Inserted.Count == 0;
                    else if (open is { } openTurn)
                        claimed.Add(openTurn);
                    break;
                }
                case TurnEndPayload turnEnd:
                {
                    open = null;
                    if (stepped.Remove(turnEnd.Turn) || claimed.Remove(turnEnd.Turn) && AccountsForClaim(turnEnd.Reason))
                    {
                        end = sessionEvent;
                        droppedUnrun = false;
                    }
                    break;
                }
            }
        }
        return new ConsumedWork(end, droppedUnrun);
    }
}
