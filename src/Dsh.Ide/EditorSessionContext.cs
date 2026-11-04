using System.Globalization;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Ide;

internal static class EditorSessionContext
{
    public static string? TurnId(Context ctx, Session? session)
        => session is null ? null : CurrentTurn(ctx, session).ToString(CultureInfo.InvariantCulture);

    public static int CurrentTurn(Context ctx, Session session)
    {
        var projection = ctx.Get<SessionProjectionRegistry>(SessionProjectionRegistry.ServiceName, false)
            ?.StateOf<TurnBoundaryProjection>(session, TurnBoundaryProjectionDefinition.Key);
        if (projection is not null)
            return projection.LastTurn;
        for (var seq = session.Seq - 1; seq >= session.FirstLiveSeq; seq--)
            if (session.EventAt(seq)?.Data is TurnStartPayload start)
                return start.Turn;
        return 0;
    }
}
