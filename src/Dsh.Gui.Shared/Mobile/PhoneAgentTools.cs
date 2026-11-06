using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
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
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName, false)
            ?? throw new InvalidOperationException("tool runtime is not available");
        return new Registrations(
        [
            tools.Register(Tool(LauncherName,
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
                args => device.LaunchApp(Text(args, "package")))),
            tools.Register(Tool(ListAppsName,
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
                args => ListApps(device, Text(args, "filter")))),
            tools.Register(Tool(OpenUrlName,
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
                args => device.OpenUrl(Text(args, "url")))),
            tools.Register(Tool(DeviceInfoName,
                "读取这台手机的型号、设备代号与 DSH 包名。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject(),
                },
                _ => PhoneActionResult.Done(device.Describe()))),
        ]);
    }

    private static PhoneActionResult ListApps(IPhoneDevice device, string? filter)
    {
        var apps = device.ListApps(filter);
        return apps.Count == 0
            ? PhoneActionResult.Done("没有匹配的应用。")
            : PhoneActionResult.Done(string.Join('\n', apps.Select(app => $"{app.Label} · {app.Package}")));
    }

    /** 输出的形状在这里写死: 交给序列化器猜属性名, render 就找不到 text。 */
    private static ToolDefinition Tool(string name, string description, JsonObject parameters, Func<JsonElement, PhoneActionResult> run)
        => new()
        {
            Name = name,
            Description = description,
            Parameters = parameters,
            Output = new ToolOutputDefinition(
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("ok", "text"),
                    ["properties"] = new JsonObject
                    {
                        ["ok"] = new JsonObject { ["type"] = "boolean", ["description"] = "调用是否成功" },
                        ["text"] = new JsonObject { ["type"] = "string", ["description"] = "给模型看的结果文本" },
                    },
                },
                (_, value) => [new TextBlock(value.GetProperty("text").GetString() ?? "")]),
            Execute = (args, _) =>
            {
                var result = run(args);
                return Task.FromResult<object?>(new JsonObject { ["ok"] = result.Ok, ["text"] = result.Text });
            },
        };

    private static string? Text(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) ? value.GetString() : null;

    private sealed class Registrations(IReadOnlyList<IDisposable> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
                item.Dispose();
        }
    }
}
