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
    bool Checked,
    bool Selected,
    int Left,
    int Top,
    int Right,
    int Bottom);

/** 一张截图: PNG 字节连同像素尺寸, 宽度给模型换算点击坐标。 */
public sealed record PhoneScreenshot(byte[] Png, int Width, int Height);

/** 截图结果: 失败时只有说明文字, 成功时带图。 */
public readonly record struct PhoneCapture(bool Ok, string Text, PhoneScreenshot? Image);

/**
 * 手机界面的自动化能力: 读当前界面、点按、长按、滑动、输入、等待、按全局键, 以及截图。
 * 平台实现走 Android 无障碍服务; 服务没开时 IsAvailable 为 false, 工具据此给出开启指引而不是假装能动。
 */
public interface IPhoneUiAutomation
{
    bool IsAvailable { get; }

    string ForegroundApp { get; }

    IReadOnlyList<PhoneUiNode> DumpUi(int limit);

    /** 截取当前屏幕; 自绘界面(淘宝购物车这类)不在无障碍树里, 只能靠这张图看。 */
    Task<PhoneCapture> CaptureAsync(CancellationToken cancellationToken);

    PhoneActionResult Tap(int x, int y);

    PhoneActionResult LongPress(int x, int y, int durationMs);

    PhoneActionResult Swipe(int fromX, int fromY, int toX, int toY, int durationMs);

    PhoneActionResult Click(int index);

    PhoneActionResult SetText(string text);

    PhoneActionResult Global(string action);

    PhoneActionResult Scroll(string direction, int times);

    Task<PhoneActionResult> WaitForAsync(string needle, int timeoutMs, CancellationToken cancellationToken);
}
