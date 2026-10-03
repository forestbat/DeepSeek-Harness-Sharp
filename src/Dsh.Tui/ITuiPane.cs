using Dsh.Core;

namespace Dsh.Tui;

/** 分割树窗格的种类: 供窗格目录/读取工具标识来源。 */
public enum TuiPaneKind
{
    Chat,
    Subagent,
    Shell,
}

/**
 * 可放进分割树的窗格视图: 交互式 ChatPane 绑定可写 agent, 只读 SubagentPane 绑定子会话, ShellPane 绑定真 PTY(shell 无 agent 会话)。
 * ChatWindow 只依赖本契约做绘制/事件/滚动路由, 输入与命令仍只走交互窗格。
 */
internal interface ITuiPane : IDisposable
{
    int Id { get; }

    TuiPaneKind Kind { get; }

    /** 绑定 agent 会话的窗格返回该会话; shell 窗格无会话, 返回 null。 */
    Session? Session { get; }

    bool StickToBottom { get; set; }

    string PaneTitle { get; }

    string StatusText { get; }

    void ProcessSessionEvent(SessionEvent sessionEvent, bool replay = false);

    void DrawTranscript(CellGrid grid, ConsoleRect rect);

    /** 点击落在 fold 头行(折叠态=预览行, 展开态=首行)时切换折叠; 命中返回 true。 */
    bool TryToggleFoldAt(int cellY, ConsoleRect rect);

    void HandleMouseWheel(float delta);

    /** 内容文本快照(线程安全拷贝, 末尾不足一行也算一行): 供窗格读取工具, 不在绘制路径上调用。 */
    IReadOnlyList<string> SnapshotLines();

    /** 该窗格是否有需要画在整帧最上层的浮层(命令/mention 菜单)。 */
    bool HasOverlay { get; }

    /** 在整帧最后一层绘制浮层; 只有 HasOverlay 为 true 时调用。 */
    void DrawOverlay(CellGrid grid, ConsoleRect rect);
}
