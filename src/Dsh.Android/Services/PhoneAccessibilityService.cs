using System.Text;
using Android.AccessibilityServices;
using Android.Graphics;
using Android.OS;
using Android.Views.Accessibility;
using AccessibilityAction = Android.Views.Accessibility.Action;
using AndroidPath = Android.Graphics.Path;
using Dsh.Gui.Shared.Mobile;

namespace Dsh.Android.Services;

/**
 * 手机界面的自动化通道: 无障碍服务读 UI 树、派发手势、给输入框写文本、按全局键。
 * 手势走 dispatchGesture, 因此不受 adb input 被 INJECT_EVENTS 拒绝的影响。
 * 服务没开时 Current 为 null, 上层据此回一句"去开启", 不假装能操作。
 */
[Service(Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
[IntentFilter(["android.accessibilityservice.AccessibilityService"])]
[MetaData("android.accessibilityservice", Resource = "@xml/phone_automation")]
public sealed class PhoneAccessibilityService : AccessibilityService
{
    private const int MaxDepth = 60;

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

    public string ForegroundPackage => RootInActiveWindow?.PackageName ?? "";

    public IReadOnlyList<PhoneUiNode> Dump(int limit)
    {
        var nodes = new List<PhoneUiNode>();
        if (RootInActiveWindow is { } root)
            Walk(root, nodes, limit);
        return nodes;
    }

    public PhoneActionResult Tap(int x, int y) => Dispatch(Stroke([(x, y)], TapMillis), $"已点击 ({x},{y})");

    public PhoneActionResult LongPress(int x, int y, int durationMs) => Dispatch(
        Stroke([(x, y)], Math.Max(durationMs, LongPressMinMillis)),
        $"已长按 ({x},{y}) {durationMs}ms");

    public PhoneActionResult Swipe(int fromX, int fromY, int toX, int toY, int durationMs) => Dispatch(
        Stroke([(fromX, fromY), (toX, toY)], Math.Max(durationMs, SwipeMinMillis)),
        $"已滑动 ({fromX},{fromY}) → ({toX},{toY})");

    public PhoneActionResult ClickNode(int index)
    {
        if (FindNode(index) is not { } pair)
            return PhoneActionResult.Fail($"没有编号为 {index} 的节点; 先 dump 一次界面");
        var (node, target) = pair;
        // 可点节点常常只有父级持有点击动作, 依次往上找两级; 都没有就退回按中心点手势点击。
        var candidate = node;
        for (var level = 0; level < ClickAncestorLevels && candidate is not null; level++)
        {
            if (candidate.PerformAction(AccessibilityAction.Click))
                return PhoneActionResult.Done($"已点击「{target.Text}」");
            candidate = candidate.Parent;
        }
        return Tap((target.Left + target.Right) / 2, (target.Top + target.Bottom) / 2);
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

    /** 滚动: 从屏幕中线往反方向滑, 一次一屏, 适合列表翻页这类连续动作。 */
    public PhoneActionResult Scroll(string direction, int times)
    {
        if (DisplayMetrics() is not { } screen)
            return PhoneActionResult.Fail("拿不到屏幕尺寸");
        var (fromX, fromY, toX, toY) = direction.ToLowerInvariant() switch
        {
            "down" => (screen.Width / 2, screen.Height * 3 / 4, screen.Width / 2, screen.Height / 4),
            "up" => (screen.Width / 2, screen.Height / 4, screen.Width / 2, screen.Height * 3 / 4),
            "left" => (screen.Width * 3 / 4, screen.Height / 2, screen.Width / 4, screen.Height / 2),
            "right" => (screen.Width / 4, screen.Height / 2, screen.Width * 3 / 4, screen.Height / 2),
            _ => (-1, -1, -1, -1),
        };
        if (fromX < 0)
            return PhoneActionResult.Fail("只支持 up / down / left / right");
        var count = Math.Clamp(times, 1, MaxScrollTimes);
        for (var done = 0; done < count; done++)
            Swipe(fromX, fromY, toX, toY, ScrollMillis);
        return PhoneActionResult.Done($"已向 {direction} 滚动 {count} 屏");
    }

    /** 等界面出现某段文字: 点完之后页面要加载, 立刻 dump 通常什么也读不到。 */
    public async Task<PhoneActionResult> WaitForAsync(string needle, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(timeoutMs, 0, MaxWaitMillis));
        do
        {
            if (needle.Length == 0 || Contains(needle))
                return PhoneActionResult.Done(needle.Length == 0 ? "界面已就绪" : $"界面上出现了「{needle}」");
            await Task.Delay(WaitPollMillis, cancellationToken).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested);
        return PhoneActionResult.Fail($"等了 {timeoutMs}ms 界面上仍没有「{needle}」");
    }

    private const int TapMillis = 60;
    private const int LongPressMinMillis = 600;
    private const int SwipeMinMillis = 120;
    private const int ScrollMillis = 300;
    private const int MaxScrollTimes = 10;
    private const int ClickAncestorLevels = 3;
    private const int MaxWaitMillis = 30_000;
    private const int WaitPollMillis = 250;
    /** 无障碍服务的 TakeScreenshot 不需要 MediaProjection 授权, 也不受 adb input 被拒的影响。 */
    public async Task<PhoneCapture> CaptureAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return new PhoneCapture(false, "无障碍截图需要 Android 11 及以上", null);
        var completion = new TaskCompletionSource<PhoneCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new ScreenshotCallback(completion);
        var executor = Java.Util.Concurrent.Executors.NewSingleThreadExecutor();
        try
        {
            TakeScreenshot(DisplayDefault, executor, callback);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
        catch (Exception error) when (error is Java.Lang.Exception or InvalidOperationException)
        {
            return new PhoneCapture(false, $"截图失败: {error.Message}", null);
        }
        finally
        {
            executor.Shutdown();
        }
    }

    private const int DisplayDefault = 0;

    /** 系统回调给的是 HardwareBuffer, 要自己裹成 Bitmap 再压成 PNG。 */
    private sealed class ScreenshotCallback(TaskCompletionSource<PhoneCapture> completion) : Java.Lang.Object, AccessibilityService.ITakeScreenshotCallback
    {
        public void OnSuccess(AccessibilityService.ScreenshotResult? result)
        {
            if (result?.HardwareBuffer is not { } buffer)
            {
                completion.TrySetResult(new PhoneCapture(false, "截图没有返回像素数据", null));
                return;
            }
            using var bitmap = Bitmap.WrapHardwareBuffer(buffer, result.ColorSpace);
            if (bitmap is null)
            {
                completion.TrySetResult(new PhoneCapture(false, "无法读取截图内容", null));
                return;
            }
            using var png = new MemoryStream();
            bitmap.Compress(Bitmap.CompressFormat.Png!, PngQuality, png);
            completion.TrySetResult(new PhoneCapture(
                true,
                $"截图 {bitmap.Width}×{bitmap.Height}",
                new PhoneScreenshot(png.ToArray(), bitmap.Width, bitmap.Height)));
        }

        public void OnFailure(int errorCode) => completion.TrySetResult(
            new PhoneCapture(false, $"系统拒绝截图 (error={errorCode})", null));
    }

    private const int PngQuality = 100;

    private bool Contains(string needle)
        => Dump(MaxDumpNodes).Any(node =>
            node.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (node.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));

    private const int MaxDumpNodes = 400;

    private (int Width, int Height)? DisplayMetrics()
    {
        if (Resources?.DisplayMetrics is not { } metrics)
            return null;
        return (metrics.WidthPixels, metrics.HeightPixels);
    }

    private static GestureDescription Stroke(IReadOnlyList<(int X, int Y)> points, int durationMs)
    {
        var path = new AndroidPath();
        path.MoveTo(points[0].X, points[0].Y);
        foreach (var (x, y) in points.Skip(1))
            path.LineTo(x, y);
        return new GestureDescription.Builder()
            .AddStroke(new GestureDescription.StrokeDescription(path, 0, durationMs))
            .Build();
    }

    /** 优先取已聚焦的输入框, 没有就取界面上第一个可写的框: 用户点过输入框才是常态。 */
    private AccessibilityNodeInfo? FindInputNode()
    {
        var fallback = (AccessibilityNodeInfo?)null;
        foreach (var node in Traverse())
        {
            if (!node.Editable)
                continue;
            if (node.Focused)
                return node;
            fallback ??= node;
        }
        return fallback;
    }

    private (AccessibilityNodeInfo Node, PhoneUiNode Target)? FindNode(int index)
    {
        var position = 0;
        foreach (var node in Traverse())
        {
            if (position++ == index)
            {
                var bounds = new Rect();
                node.GetBoundsInScreen(bounds);
                return (node, Describe(node, index, bounds));
            }
        }
        return null;
    }

    private IEnumerable<AccessibilityNodeInfo> Traverse(AccessibilityNodeInfo? start = null)
    {
        var root = start ?? RootInActiveWindow;
        if (root is null)
            yield break;
        var queue = new Queue<(AccessibilityNodeInfo Node, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            yield return node;
            if (depth >= MaxDepth)
                continue;
            for (var child = 0; child < node.ChildCount; child++)
            {
                if (node.GetChild(child) is { } next)
                    queue.Enqueue((next, depth + 1));
            }
        }
    }

    private void Walk(AccessibilityNodeInfo node, List<PhoneUiNode> nodes, int limit)
    {
        foreach (var current in Traverse(node))
        {
            if (nodes.Count >= limit)
                return;
            var bounds = new Rect();
            current.GetBoundsInScreen(bounds);
            nodes.Add(Describe(current, nodes.Count, bounds));
        }
    }

    private static PhoneUiNode Describe(AccessibilityNodeInfo node, int index, Rect bounds)
        => new(
            index,
            (node.Text ?? "").Trim(),
            node.ContentDescription,
            node.ClassName,
            node.ViewIdResourceName,
            node.Clickable,
            node.Editable,
            node.Scrollable,
            node.Checked,
            node.Selected,
            bounds.Left,
            bounds.Top,
            bounds.Right,
            bounds.Bottom);

    private PhoneActionResult Dispatch(GestureDescription gesture, string success)
        => DispatchGesture(gesture, new Completed(), null)
            ? PhoneActionResult.Done(success)
            : PhoneActionResult.Fail("系统拒绝了这次手势; 确认无障碍服务仍在运行");

    private sealed class Completed : GestureResultCallback
    {
    }
}
