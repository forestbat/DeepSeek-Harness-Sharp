using System.Text;
using Android.AccessibilityServices;
using Android.Graphics;
using AndroidPath = Android.Graphics.Path;
using Android.OS;
using Android.Views.Accessibility;
using AccessibilityAction = Android.Views.Accessibility.Action;
using Dsh.Gui.Shared.Mobile;

namespace Dsh.Android.Services;

/**
 * 手机界面的自动化通道: 无障碍服务读 UI 树、派发手势、给输入框写文本、按全局键。
 * 服务由用户在系统设置里开启, 没开时 Current 为 null, 上层据此回一句"去开启", 不假装能操作。
 */
[Service(Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
[IntentFilter(["android.accessibilityservice.AccessibilityService"])]
[MetaData("android.accessibilityservice", Resource = "@xml/phone_automation")]
public sealed class PhoneAccessibilityService : AccessibilityService
{
    private const int MaxDepth = 40;

    public static PhoneAccessibilityService? Current { get; private set; }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Current = this;
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
    }

    public override void OnInterrupt()
    {
    }

    public override bool OnUnbind(global::Android.Content.Intent? intent)
    {
        Current = null;
        return base.OnUnbind(intent);
    }

    public override void OnDestroy()
    {
        Current = null;
        base.OnDestroy();
    }

    public string ForegroundPackage
        => RootInActiveWindow?.PackageName ?? "";

    public IReadOnlyList<PhoneUiNode> Dump(int limit)
    {
        var nodes = new List<PhoneUiNode>();
        if (RootInActiveWindow is { } root)
            Walk(root, nodes, limit);
        return nodes;
    }

    public PhoneActionResult Tap(int x, int y)
    {
        var path = new AndroidPath();
        path.MoveTo(x, y);
        var gesture = new GestureDescription.Builder()
            .AddStroke(new GestureDescription.StrokeDescription(path, 0, TapMillis))
            .Build();
        return Dispatch(gesture, $"已点击 ({x},{y})");
    }

    public PhoneActionResult Swipe(int fromX, int fromY, int toX, int toY, int durationMs)
    {
        var path = new AndroidPath();
        path.MoveTo(fromX, fromY);
        path.LineTo(toX, toY);
        var gesture = new GestureDescription.Builder()
            .AddStroke(new GestureDescription.StrokeDescription(path, 0, Math.Max(durationMs, SwipeMinMillis)))
            .Build();
        return Dispatch(gesture, $"已滑动 ({fromX},{fromY}) → ({toX},{toY})");
    }

    public PhoneActionResult ClickNode(int index)
    {
        var nodes = Dump(int.MaxValue);
        if (nodes.FirstOrDefault(node => node.Index == index) is not { } target)
            return PhoneActionResult.Fail($"没有编号为 {index} 的节点; 先 dump 一次界面");
        if (FindNode(target) is not { } node)
            return PhoneActionResult.Fail($"编号 {index} 的节点已经不在界面上; 重新 dump 一次");
        if (node.PerformAction(AccessibilityAction.Click) || node.Parent?.PerformAction(AccessibilityAction.Click) is true)
            return PhoneActionResult.Done($"已点击「{target.Text}」");
        var x = (target.Left + target.Right) / 2;
        var y = (target.Top + target.Bottom) / 2;
        return Tap(x, y);
    }

    public PhoneActionResult SetText(string text)
    {
        if (FindInputNode() is not { } input)
            return PhoneActionResult.Fail("当前界面没有可写的输入框; 先用 tap 点一下输入框");
        var arguments = new Bundle();
        arguments.PutCharSequence(AccessibilityNodeInfo.ActionArgumentSetTextCharsequence, text);
        return input.PerformAction(AccessibilityAction.SetText, arguments)
            ? PhoneActionResult.Done($"已输入 {text.Length} 个字符")
            : PhoneActionResult.Fail("这个输入框不接受程序写入; 换 tap 之后再试");
    }

    public PhoneActionResult Global(string action)
    {
        var mapped = action.ToLowerInvariant() switch
        {
            "back" => GlobalAction.Back,
            "home" => GlobalAction.Home,
            "recents" => GlobalAction.Recents,
            "notifications" => GlobalAction.Notifications,
            _ => (GlobalAction)(-1),
        };
        if ((int)mapped < 0)
            return PhoneActionResult.Fail("只支持 back / home / recents / notifications");
        return PerformGlobalAction(mapped)
            ? PhoneActionResult.Done($"已执行 {action}")
            : PhoneActionResult.Fail($"系统拒绝了 {action}");
    }

    private const int TapMillis = 60;
    private const int SwipeMinMillis = 120;

    /** 优先取已聚焦的输入框, 没有就取界面上第一个可写的框: 用户点过输入框才是常态。 */
    private AccessibilityNodeInfo? FindInputNode()
    {
        var queue = new Queue<(AccessibilityNodeInfo Node, int Depth)>();
        if (RootInActiveWindow is { } root)
            queue.Enqueue((root, 0));
        AccessibilityNodeInfo? fallback = null;
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (node.Editable)
            {
                if (node.Focused)
                    return node;
                fallback ??= node;
            }
            if (depth >= MaxDepth)
                continue;
            for (var child = 0; child < node.ChildCount; child++)
            {
                if (node.GetChild(child) is { } next)
                    queue.Enqueue((next, depth + 1));
            }
        }
        return fallback;
    }

    private AccessibilityNodeInfo? FindNode(PhoneUiNode target)
    {
        var queue = new Queue<(AccessibilityNodeInfo Node, int Depth)>();
        if (RootInActiveWindow is { } root)
            queue.Enqueue((root, 0));
        var index = 0;
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (index++ == target.Index)
                return node;
            if (depth >= MaxDepth)
                continue;
            for (var child = 0; child < node.ChildCount; child++)
            {
                if (node.GetChild(child) is { } next)
                    queue.Enqueue((next, depth + 1));
            }
        }
        return null;
    }

    private void Walk(AccessibilityNodeInfo node, List<PhoneUiNode> nodes, int limit)
    {
        var queue = new Queue<(AccessibilityNodeInfo Node, int Depth)>();
        queue.Enqueue((node, 0));
        while (queue.Count > 0 && nodes.Count < limit)
        {
            var (current, level) = queue.Dequeue();
            var bounds = new Rect();
            current.GetBoundsInScreen(bounds);
            nodes.Add(new PhoneUiNode(
                nodes.Count,
                (current.Text ?? current.ContentDescription ?? "").Trim(),
                current.ContentDescription,
                current.ClassName,
                current.ViewIdResourceName,
                current.Clickable,
                current.Editable,
                current.Scrollable,
                bounds.Left,
                bounds.Top,
                bounds.Right,
                bounds.Bottom));
            if (level >= MaxDepth)
                continue;
            for (var child = 0; child < current.ChildCount; child++)
            {
                if (current.GetChild(child) is { } next)
                    queue.Enqueue((next, level + 1));
            }
        }
    }

    private PhoneActionResult Dispatch(GestureDescription gesture, string success)
        => DispatchGesture(gesture, new Completed(), null)
            ? PhoneActionResult.Done(success)
            : PhoneActionResult.Fail("系统拒绝了这次手势; 确认无障碍服务仍在运行");

    private sealed class Completed : GestureResultCallback
    {
    }
}
