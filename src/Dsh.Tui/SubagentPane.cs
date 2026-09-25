using Dsh.Core;
using Dsh.Subagent;

namespace Dsh.Tui;

/**
 * 只读子代理查看窗格: 绑定一个子会话, 跟踪渲染其事件流, 底部提供 Parent/Prev/Next 兄弟导航。
 * 不持有输入缓冲; 除导航键外的按键由 ChatWindow 继续路由到主交互窗格。
 */
internal sealed class SubagentPane : ITuiPane
{
    private const int FooterHeight = 1;

    private readonly ChatWindow _window;
    private readonly SubagentDirectory _directory;
    private Session _session;
    private int _wrapVersion = -1;
    private int _wrapWidth = -1;
    private List<string>? _wrapLines;

    public SubagentPane(ChatWindow window, int id, Session session, SubagentDirectory directory)
    {
        _window = window;
        _directory = directory;
        _session = session;
        Id = id;
        ReplayEvents();
    }

    public int Id { get; }

    public Session Session => _session;

    private TranscriptRenderer Renderer { get; set; } = new();

    private long RenderedSeq { get; set; }

    public bool StickToBottom { get; set; } = true;

    public int ScrollOffset { get; set; }

    public string PaneTitle => $"子代理 {Label}";

    public string StatusText => "子代理查看: ←/→ 兄弟 · ↑ 父会话 · Esc 关闭";

    private string Label => SubagentDescriptorPayload.IdentityOf(_session)?.Label ?? _session.Id.Value;

    public void ProcessSessionEvent(SessionEvent sessionEvent, bool replay = false)
    {
        if (sessionEvent.Seq < RenderedSeq)
            return;
        RenderedSeq = sessionEvent.Seq + 1;
        Renderer.AppendSessionEvent(sessionEvent, replay);
    }

    public void ReplayEvents()
    {
        foreach (var sessionEvent in _session.SnapshotEvents())
            ProcessSessionEvent(sessionEvent, replay: true);
    }

    public bool HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:
                GoSibling(-1);
                return true;
            case ConsoleKey.RightArrow:
                GoSibling(1);
                return true;
            case ConsoleKey.UpArrow:
                GoParent();
                return true;
            case ConsoleKey.Escape:
                _window.CloseSubagentView(this);
                return true;
            default:
                return false;
        }
    }

    public void HandleMouseWheel(int delta)
    {
        if (delta > 0)
        {
            StickToBottom = false;
            ScrollOffset = Math.Max(0, ScrollOffset - 10);
        }
        else if (delta < 0)
        {
            ScrollOffset += 10;
        }
    }

    public void DrawTranscript(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var bodyHeight = Math.Max(0, rect.Height - FooterHeight);
        if (bodyHeight > 0)
            DrawBody(grid, new ConsoleRect(rect.X, rect.Y, rect.Width, bodyHeight));
        DrawFooter(grid, new ConsoleRect(rect.X, rect.Bottom - 1, rect.Width, 1));
    }

    private void DrawBody(CellGrid grid, ConsoleRect rect)
    {
        var wrapped = Wrapped(rect.Width);
        var maxOffset = Math.Max(0, wrapped.Count - rect.Height);
        if (StickToBottom)
            ScrollOffset = maxOffset;
        ScrollOffset = Math.Clamp(ScrollOffset, 0, maxOffset);
        for (var row = 0; row < rect.Height; row++)
        {
            var index = ScrollOffset + row;
            if (index >= wrapped.Count)
                break;
            CellText.Draw(grid, rect.X, rect.Y + row, wrapped[index]);
        }
    }

    private void DrawFooter(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        var state = _window.IsLive(_session.Id) ? "● live" : "○ ended";
        CellText.Draw(
            grid,
            rect.X,
            rect.Y,
            $"子代理 {Label} [{state}]{SiblingPosition()} · ◀/▶ 兄弟 · ↑ 父会话 · Esc 关闭",
            AnsiColor.Default,
            AnsiColor.Default,
            CellStyle.Dim);
    }

    /** 兄弟序号对齐 kilocode SubagentFooter 的 N of M; 根级或无兄弟时不显示。 */
    private string SiblingPosition()
    {
        if (_session.Header.ParentSession is not { } parentId)
            return "";
        var siblings = _directory.Children(parentId);
        if (siblings.Count <= 1)
            return "";
        for (var index = 0; index < siblings.Count; index++)
        {
            if (siblings[index].Id == _session.Id)
                return $" · 兄弟 {index + 1}/{siblings.Count}";
        }
        return "";
    }

    private IReadOnlyList<string> Wrapped(int width)
    {
        var version = Renderer.Version;
        if (_wrapLines is not null && _wrapVersion == version && _wrapWidth == width)
            return _wrapLines;

        var lines = new List<string>();
        foreach (var line in Renderer.CompletedLines)
            ChatPane.WrapSingleLine(line, width, lines);
        if (Renderer.Tail.Length > 0)
            ChatPane.WrapSingleLine(Renderer.Tail, width, lines);
        if (lines.Count == 0)
            lines.Add("");

        _wrapLines = lines;
        _wrapVersion = version;
        _wrapWidth = width;
        return lines;
    }

    private void GoParent()
    {
        if (_session.Header.ParentSession is not { } parentId)
            return;
        var parent = _directory.FindSession(parentId);
        if (parent is not null && SubagentDescriptorPayload.IdentityOf(parent) is not null)
            NavigateTo(parent);
        else
            _window.CloseSubagentView(this, parentId);
    }

    private void GoSibling(int step)
    {
        if (_session.Header.ParentSession is not { } parentId)
            return;
        var siblings = _directory.Children(parentId);
        if (siblings.Count <= 1)
            return;
        var index = -1;
        for (var position = 0; position < siblings.Count; position++)
        {
            if (siblings[position].Id == _session.Id)
            {
                index = position;
                break;
            }
        }
        if (index < 0)
            return;
        var target = siblings[(index + step + siblings.Count) % siblings.Count];
        if (_directory.FindSession(target.Id) is { } session)
            NavigateTo(session);
    }

    private void NavigateTo(Session session)
    {
        _session = session;
        Renderer = new TranscriptRenderer();
        RenderedSeq = 0;
        ScrollOffset = 0;
        StickToBottom = true;
        _wrapLines = null;
        _wrapVersion = -1;
        _wrapWidth = -1;
        ReplayEvents();
    }

    public void Dispose()
    {
    }
}
