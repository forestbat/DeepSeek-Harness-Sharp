namespace Dsh.Tui;

/**
 * `/timestamp` 的时间线浮层: 一级选消息(含 IDE 历史项), 二级选动作(jump/revert/fork)。
 * 只在打开期间存在; 选中动作后回调到 ChatWindow 的既有执行路径, 浮层自己关掉。
 */
internal sealed class TimelineOverlay
{
    public static readonly string[] Actions = ["jump", "revert", "fork"];

    private IReadOnlyList<CommandCandidate> _all = [];
    private List<CommandCandidate> _visible = [];
    private string _filter = "";

    public bool IsActive { get; private set; }

    /** true: 正在选动作(二级); false: 正在选消息(一级)。 */
    public bool PickingAction { get; private set; }

    /** revert 需要二次确认: 大动作会截断会话并回滚文件。 */
    public bool AwaitingConfirm { get; private set; }

    public int Index { get; private set; }

    public int ActionIndex { get; private set; }

    public IReadOnlyList<CommandCandidate> Candidates => _visible;

    public IReadOnlyList<string> ActionLabels => Actions;

    public CommandCandidate? Selected => _visible.Count == 0 ? null : _visible[Math.Clamp(Index, 0, _visible.Count - 1)];

    public string Title => PickingAction
        ? $"/timestamp {Actions[Math.Clamp(ActionIndex, 0, Actions.Length - 1)]} {Selected?.Value}"
        : $"/timestamp 时间线 ({_visible.Count})";

    public string Filter => _filter;

    public void Open(IReadOnlyList<CommandCandidate> candidates)
    {
        _all = candidates;
        _filter = "";
        PickingAction = false;
        AwaitingConfirm = false;
        // 候选按时间倒序(最新在前), 默认落在最新一条上。
        Index = 0;
        ApplyFilter();
        IsActive = true;
    }

    public void Close()
    {
        IsActive = false;
        PickingAction = false;
        AwaitingConfirm = false;
        _filter = "";
        _all = [];
        _visible = [];
    }

    public void Back()
    {
        if (AwaitingConfirm)
        {
            AwaitingConfirm = false;
            return;
        }
        if (PickingAction)
        {
            PickingAction = false;
            return;
        }
        Close();
    }

    public void Move(int delta)
    {
        if (!IsActive)
            return;
        if (PickingAction)
            ActionIndex = Math.Clamp(ActionIndex + delta, 0, Actions.Length - 1);
        else if (_visible.Count > 0)
            Index = Math.Clamp(Index + delta, 0, _visible.Count - 1);
    }

    /** 一级: 过滤候选(按 label/description 子串); 只在选消息阶段生效。 */
    public void AppendFilter(char character)
    {
        if (!IsActive || PickingAction)
            return;
        _filter += character;
        ApplyFilter();
    }

    public void BackspaceFilter()
    {
        if (!IsActive || PickingAction || _filter.Length == 0)
            return;
        _filter = _filter[..^1];
        ApplyFilter();
    }

    /** 一级回车: 进入动作选择; 二级回车: 返回要执行的动作(需要确认时先置 AwaitingConfirm)。 */
    public string? ConfirmSelection()
    {
        if (!IsActive)
            return null;
        if (AwaitingConfirm)
            return null;
        if (!PickingAction)
        {
            if (Selected is null)
                return null;
            PickingAction = true;
            ActionIndex = 0;
            return null;
        }
        var action = Actions[Math.Clamp(ActionIndex, 0, Actions.Length - 1)];
        if (action == "revert" && !AwaitingConfirm)
        {
            AwaitingConfirm = true;
            return null;
        }
        return action;
    }

    /** 确认框 y/n: true 表示用户按了 y。 */
    public bool AnswerConfirm(bool accepted)
    {
        AwaitingConfirm = false;
        return accepted;
    }

    private void ApplyFilter()
    {
        _visible = _filter.Length == 0
            ? [.. _all]
            : [.. _all.Where(candidate =>
                candidate.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || candidate.Value.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || (candidate.Description?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false))];
        Index = Math.Clamp(Index, 0, Math.Max(0, _visible.Count - 1));
    }
}
