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

    public static IDisposable Register(Context ctx, IPhoneDevice device)
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
        ]);
    }

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
