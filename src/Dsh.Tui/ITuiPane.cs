using Dsh.Core;

namespace Dsh.Tui;

/**
 * 可放进分割树的窗格视图: 交互式 ChatPane 绑定可写 agent, 只读 SubagentPane 绑定子会话。
 * ChatWindow 只依赖本契约做绘制/事件/滚动路由, 输入与命令仍只走交互窗格。
 */
internal interface ITuiPane : IDisposable
{
    int Id { get; }

    Session Session { get; }

    bool StickToBottom { get; set; }

    string PaneTitle { get; }

    string StatusText { get; }

    void ProcessSessionEvent(SessionEvent sessionEvent, bool replay = false);

    void DrawTranscript(CellGrid grid, ConsoleRect rect);

    void HandleMouseWheel(int delta);
}
