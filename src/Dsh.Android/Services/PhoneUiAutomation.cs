using Dsh.Gui.Shared.Mobile;

namespace Dsh.Android.Services;

/** 无障碍服务的门面: 服务没开时每件事都回一句"去开启", 让模型知道缺什么而不是瞎猜。 */
public sealed class PhoneUiAutomation : IPhoneUiAutomation
{
    private const string NotRunning = "无障碍服务没有开启: 到系统设置 → 无障碍 → 已下载的服务里打开 DSH, 再重试";

    public bool IsAvailable => PhoneAccessibilityService.Current is not null;

    public string ForegroundApp => PhoneAccessibilityService.Current?.ForegroundPackage ?? "";

    public IReadOnlyList<PhoneUiNode> DumpUi(int limit)
        => PhoneAccessibilityService.Current?.Dump(limit) ?? [];

    public PhoneActionResult Tap(int x, int y)
        => Current()?.Tap(x, y) ?? PhoneActionResult.Fail(NotRunning);

    public PhoneActionResult Swipe(int fromX, int fromY, int toX, int toY, int durationMs)
        => Current()?.Swipe(fromX, fromY, toX, toY, durationMs) ?? PhoneActionResult.Fail(NotRunning);

    public PhoneActionResult Click(int index)
        => Current()?.ClickNode(index) ?? PhoneActionResult.Fail(NotRunning);

    public PhoneActionResult SetText(string text)
        => Current()?.SetText(text) ?? PhoneActionResult.Fail(NotRunning);

    public PhoneActionResult Global(string action)
        => Current()?.Global(action) ?? PhoneActionResult.Fail(NotRunning);

    private static PhoneAccessibilityService? Current() => PhoneAccessibilityService.Current;
}
