using System.Text.Json.Nodes;

namespace Dsh.Gui.Shared.Mobile;

/** UI 树上的一个节点: 序号给工具回指, 文本给模型认路, bounds 给点击用。 */
public sealed record PhoneUiNode(
    int Index,
    string Text,
    string? Description,
    string? Kind,
    string? Id,
    bool Clickable,
    bool Editable,
    bool Scrollable,
    int Left,
    int Top,
    int Right,
    int Bottom);

/**
 * 手机界面的自动化能力: 读当前界面、点按、滑动、输入、返回。
 * 平台实现走 Android 无障碍服务; 服务没开时 IsAvailable 为 false, 工具据此给出开启指引而不是假装能动。
 */
public interface IPhoneUiAutomation
{
    bool IsAvailable { get; }

    string ForegroundApp { get; }

    IReadOnlyList<PhoneUiNode> DumpUi(int limit);

    PhoneActionResult Tap(int x, int y);

    PhoneActionResult Swipe(int fromX, int fromY, int toX, int toY, int durationMs);

    PhoneActionResult Click(int index);

    PhoneActionResult SetText(string text);

    PhoneActionResult Global(string action);
}
