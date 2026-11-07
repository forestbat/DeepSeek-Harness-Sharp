using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Gui.Shared.Mobile;

/**
 * 把手机自身的能力注册成本机 agent 的原生工具。
 * 与 MCP 服务端共用同一份 IPhoneDevice 语义, 只是这里直接进 ToolRuntime, 不需要网络与协议。
 */
public static class PhoneAgentTools
{
    public const string LauncherName = "android_launch_app";
    public const string ListAppsName = "android_list_apps";
    public const string OpenUrlName = "android_open_url";
    public const string DeviceInfoName = "android_device_info";
    public const string DumpUiName = "android_ui_dump";
    public const string TapName = "android_ui_tap";
    public const string SwipeName = "android_ui_swipe";
    public const string InputName = "android_ui_input";
    public const string KeyName = "android_ui_key";

    public static IDisposable Register(Context ctx, IPhoneDevice device, IPhoneUiAutomation automation)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("tool runtime is not available");
        return new Registrations(
        [
            tools.Register(MobileTool.Create(LauncherName,
                "按包名启动手机上的应用; 先用 android_list_apps 查包名。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("package"),
                    ["properties"] = new JsonObject
                    {
                        ["package"] = new JsonObject { ["type"] = "string", ["description"] = "Android 包名，例如 com.miui.calculator" },
                    },
                },
                (args, _) => Answered(device.LaunchApp(MobileTool.Text(args, "package"))))),
            tools.Register(MobileTool.Create(ListAppsName,
                "列出手机上可启动的应用; filter 按包名或应用名做不区分大小写的子串匹配。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["filter"] = new JsonObject { ["type"] = "string", ["description"] = "过滤词，可省略" },
                    },
                },
                (args, _) => Answered(ListApps(device, MobileTool.Text(args, "filter"))))),
            tools.Register(MobileTool.Create(OpenUrlName,
                "用系统默认浏览器打开一个 http/https 链接。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("url"),
                    ["properties"] = new JsonObject
                    {
                        ["url"] = new JsonObject { ["type"] = "string", ["description"] = "要打开的链接" },
                    },
                },
                (args, _) => Answered(device.OpenUrl(MobileTool.Text(args, "url"))))),
            tools.Register(MobileTool.Create(DeviceInfoName,
                "读取这台手机的型号、设备代号与 DSH 包名。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject(),
                },
                (_, _) => Answered(PhoneActionResult.Done(device.Describe())))),
            tools.Register(MobileTool.Create(DumpUiName,
                "读取当前界面: 每个节点给出编号、文本、类型与屏幕坐标; 点按优先用编号。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "最多返回多少节点，默认 60" },
                    },
                },
                (args, _) => Answered(DumpUi(automation, args)))),
            tools.Register(MobileTool.Create(TapName,
                "点按当前界面; 给 index 点某个节点, 或给 x/y 点坐标。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["index"] = new JsonObject { ["type"] = "integer", ["description"] = "android_ui_dump 里的编号" },
                        ["x"] = new JsonObject { ["type"] = "integer", ["description"] = "屏幕 x 坐标" },
                        ["y"] = new JsonObject { ["type"] = "integer", ["description"] = "屏幕 y 坐标" },
                    },
                },
                (args, _) => Answered(Tap(automation, args)))),
            tools.Register(MobileTool.Create(SwipeName,
                "在屏幕上滑动, 用来滚动列表或翻页。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("from_x", "from_y", "to_x", "to_y"),
                    ["properties"] = new JsonObject
                    {
                        ["from_x"] = new JsonObject { ["type"] = "integer", ["description"] = "起点 x" },
                        ["from_y"] = new JsonObject { ["type"] = "integer", ["description"] = "起点 y" },
                        ["to_x"] = new JsonObject { ["type"] = "integer", ["description"] = "终点 x" },
                        ["to_y"] = new JsonObject { ["type"] = "integer", ["description"] = "终点 y" },
                        ["duration_ms"] = new JsonObject { ["type"] = "integer", ["description"] = "滑动时长，默认 300" },
                    },
                },
                (args, _) => Answered(Swipe(automation, args)))),
            tools.Register(MobileTool.Create(InputName,
                "往当前聚焦的输入框写文本; 先 android_ui_tap 点中输入框。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("text"),
                    ["properties"] = new JsonObject
                    {
                        ["text"] = new JsonObject { ["type"] = "string", ["description"] = "要写入的文本" },
                    },
                },
                (args, _) => Answered(automation.SetText(MobileTool.Text(args, "text") ?? "")))),
            tools.Register(MobileTool.Create(KeyName,
                "按系统键: back / home / recents / notifications。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("action"),
                    ["properties"] = new JsonObject
                    {
                        ["action"] = new JsonObject { ["type"] = "string", ["description"] = "back / home / recents / notifications" },
                    },
                },
                (args, _) => Answered(automation.Global(MobileTool.Text(args, "action") ?? "back")))),
        ]);
    }

    private const int DefaultDumpLimit = 60;

    /** 无障碍服务没开时如实说明, 让模型去要求用户开启, 而不是反复试错。 */
    private static PhoneActionResult DumpUi(IPhoneUiAutomation automation, System.Text.Json.JsonElement args)
    {
        if (!automation.IsAvailable)
            return PhoneActionResult.Fail("无障碍服务没有开启: 到系统设置 → 无障碍里打开 DSH, 之后才能读界面、点按、输入");
        var limit = args.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var parsed)
            ? Math.Clamp(parsed, 1, 400)
            : DefaultDumpLimit;
        var nodes = automation.DumpUi(limit);
        if (nodes.Count == 0)
            return PhoneActionResult.Fail("当前界面没有可读节点");
        var lines = nodes.Select(node => $"{node.Index}) {Describe(node)}");
        return PhoneActionResult.Done($"前台应用: {automation.ForegroundApp}\n{string.Join('\n', lines)}");
    }

    private static string Describe(PhoneUiNode node)
    {
        var flags = new List<string>();
        if (node.Clickable)
            flags.Add("可点");
        if (node.Editable)
            flags.Add("可输入");
        if (node.Scrollable)
            flags.Add("可滚动");
        var label = node.Text.Length > 0
            ? node.Text
            : node.Description is { Length: > 0 } description
                ? description
                : $"<{node.Kind?.Split('.').LastOrDefault() ?? "节点"}>";
        var suffix = flags.Count > 0 ? $"（{string.Join("/", flags)}）" : "";
        return $"{label}{suffix} [{node.Left},{node.Top}]-[{node.Right},{node.Bottom}] {node.Id ?? ""}".TrimEnd();
    }

    private static PhoneActionResult Tap(IPhoneUiAutomation automation, System.Text.Json.JsonElement args)
    {
        if (args.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var index))
            return automation.Click(index);
        if (args.TryGetProperty("x", out var xElement) && xElement.TryGetInt32(out var x)
            && args.TryGetProperty("y", out var yElement) && yElement.TryGetInt32(out var y))
            return automation.Tap(x, y);
        return PhoneActionResult.Fail("需要 index, 或者 x 与 y");
    }

    private static PhoneActionResult Swipe(IPhoneUiAutomation automation, System.Text.Json.JsonElement args)
    {
        if (!args.TryGetProperty("from_x", out var fromX) || !fromX.TryGetInt32(out var x1)
            || !args.TryGetProperty("from_y", out var fromY) || !fromY.TryGetInt32(out var y1)
            || !args.TryGetProperty("to_x", out var toX) || !toX.TryGetInt32(out var x2)
            || !args.TryGetProperty("to_y", out var toY) || !toY.TryGetInt32(out var y2))
            return PhoneActionResult.Fail("需要 from_x / from_y / to_x / to_y");
        var duration = args.TryGetProperty("duration_ms", out var durationElement) && durationElement.TryGetInt32(out var parsed)
            ? parsed
            : DefaultSwipeMillis;
        return automation.Swipe(x1, y1, x2, y2, duration);
    }

    private const int DefaultSwipeMillis = 300;

    private static Task<MobileToolAnswer> Answered(PhoneActionResult result)
        => Task.FromResult(new MobileToolAnswer(result.Ok, result.Text));

    private static PhoneActionResult ListApps(IPhoneDevice device, string? filter)
    {
        var apps = device.ListApps(filter);
        return apps.Count == 0
            ? PhoneActionResult.Done("没有匹配的应用。")
            : PhoneActionResult.Done(string.Join('\n', apps.Select(app => $"{app.Label} · {app.Package}")));
    }

    private sealed class Registrations(IReadOnlyList<IDisposable> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
                item.Dispose();
        }
    }
}
