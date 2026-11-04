using Dsh.Runtime;
using Dsh.Llm;
using Dsh.Runtime.Events;

namespace Dsh.Core;

public sealed class RuntimeContextProjection : IDisposable
{
    /** 运行期上下文快照的消息来源; agent loop 在请求投影里按这个来源把快照并回用户消息。 */
    public const string Source = "@deepseek-ai/dsh-system-prompt";
    private const string Cleared = "Current runtime context: none. Earlier runtime-context snapshots no longer apply.";

    private (long Seq, string? Text)? _retained;
    private bool _neverExisted;
    private readonly Func<bool> _unsubscribe;
    private readonly Func<bool> _unsubscribeTruncate;

    public RuntimeContextProjection(Context ctx, Session session)
    {
        Rebuild(session);
        // 截断可能删掉已保留的快照; 不重建的话 Project 仍以为模型看得到它, 截断后的请求就缺了运行时上下文。
        _unsubscribeTruncate = ctx.On<SessionTruncateNotification>(notification =>
        {
            if (ReferenceEquals(notification.Session, session))
                Rebuild(session);
        });
        _unsubscribe = ctx.On<SessionEventNotification>(notification =>
        {
            if (!ReferenceEquals(notification.Session, session))
                return;
            var sessionEvent = notification.Event;
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
        });
    }

    public void Dispose()
    {
        _unsubscribe();
        _unsubscribeTruncate();
    }

    private void Rebuild(Session session)
    {
        _retained = null;
        _neverExisted = true;
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
