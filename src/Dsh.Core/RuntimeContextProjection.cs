using Cordis;
using Dsh.Llm;

namespace Dsh.Core;

public sealed class RuntimeContextProjection
{
    private const string Source = "@deepseek-ai/dsh-system-prompt";
    private const string Cleared = "Current runtime context: none. Earlier runtime-context snapshots no longer apply.";

    private (long Seq, string? Text)? _retained;
    private bool _neverExisted = true;

    public RuntimeContextProjection(Context ctx, Session session)
    {
        var surface = new HashSet<long>(session.SurfaceManager.Nodes);
        for (var index = session.Seq - 1; index >= 0; index--)
        {
            if (session.EventAt(index) is not { Data: UserMessagePayload userMessage } sessionEvent
                || !IsOwned(userMessage.Message))
                continue;
            _neverExisted = false;
            if (surface.Contains(sessionEvent.Seq))
            {
                _retained = (sessionEvent.Seq, TextOf(userMessage.Message));
                break;
            }
        }
        ctx.On("session/event", (thisArg, args) =>
        {
            if (!ReferenceEquals(args[0], session))
                return new ValueTask<object?>();
            var sessionEvent = (SessionEvent)args[1]!;
            if (sessionEvent.Data is UserMessagePayload message && IsOwned(message.Message))
            {
                _neverExisted = false;
                _retained = (sessionEvent.Seq, TextOf(message.Message));
            }
            else if (_retained is { } retained
                && sessionEvent is { SurfaceOp: SurfaceOp.Replace } replacement
                && replacement.SourceEventSeqs?.Contains(retained.Seq) == true)
            {
                _retained = null;
            }
            return new ValueTask<object?>();
        });
    }

    private static bool IsOwned(UserMessage message)
        => message.Source is PluginMessageSource { Plugin: Source };

    private static string? TextOf(UserMessage message)
        => message.Content is [TextBlock text] ? text.Text : null;

    public UserMessage? Project(string current, IReadOnlyList<ContextSnapshotSection> sections)
    {
        if (_neverExisted && _retained is null && current.Length == 0)
            return null;
        var snapshot = current.Length == 0 ? Cleared : current;
        if (_retained?.Text == snapshot)
            return null;
        var source = sections.Count == 0
            ? new PluginMessageSource(Source)
            : new PluginMessageSource(Source, ContextForms.Snapshot, sections);
        return MessageFactory.CreateUserMessage([new TextBlock(snapshot)], source);
    }
}
