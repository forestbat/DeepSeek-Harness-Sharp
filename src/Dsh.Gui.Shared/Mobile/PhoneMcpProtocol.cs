using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Gui.Shared.Mobile;

/** 手机自身的能力边界: 平台实现负责真机 API, 协议层只做 JSON-RPC 与工具表。 */
public interface IPhoneDevice
{
    string Describe();

    IReadOnlyList<PhoneApp> ListApps(string? filter);

    PhoneActionResult LaunchApp(string package);

    PhoneActionResult OpenUrl(string url);
}

public sealed record PhoneApp(string Package, string Label);

public sealed record PhoneActionResult(bool Ok, string Text)
{
    public static PhoneActionResult Fail(string text) => new(false, text);

    public static PhoneActionResult Done(string text) => new(true, text);
}

/**
 * MCP 的无状态服务端: 只实现 initialize / tools / ping 这些电脑端 agent 真正会用到的方法。
 * 手机工具一律走 tools/call, 平台差异留在 IPhoneDevice 后面, 协议本身可以在桌面上被测。
 */
public sealed class PhoneMcpProtocol(IPhoneDevice device, IPhoneUiAutomation? automation = null)
{
    public const string ServerName = "dsh-phone";
    public const string ServerVersion = "0.4.0";

    private const string DefaultProtocolVersion = "2026-07-28";

    private const string Instructions =
        "这些工具操作的是手机本身: 列出已安装应用、按包名启动应用、用浏览器打开链接。";

    /** 返回 null 表示这是一条通知, 按 MCP 约定不需要响应体。 */
    public string? Handle(string body)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return Failure(null, -32700, "无法解析的 JSON").ToJsonString();
        }
        if (node is not JsonObject request)
            return Failure(null, -32600, "请求必须是 JSON 对象").ToJsonString();
        var id = request["id"]?.DeepClone();
        // 通知(没有 id)一律不回应, 包括 notifications/initialized。
        if (id is null)
            return null;
        var method = TextOf(request["method"]);
        var parameters = request["params"] as JsonObject;
        var response = method switch
        {
            "initialize" => Success(id, Initialize(parameters)),
            "ping" => Success(id, new JsonObject()),
            "tools/list" => Success(id, Tools()),
            "tools/call" => ToolCall(id, parameters),
            _ => Failure(id, -32601, $"不支持的方法: {method}"),
        };
        return response.ToJsonString();
    }

    private static JsonObject Initialize(JsonObject? parameters)
    {
        // 客户端给什么版本就回什么版本: 本服务只用 initialize/tools 这些长期稳定的形状。
        var requested = TextOf(parameters?["protocolVersion"]);
        return new JsonObject
        {
            ["protocolVersion"] = requested is { Length: > 0 } ? requested : DefaultProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
            },
            ["instructions"] = Instructions,
        };
    }

    private static JsonObject Tools() => new()
    {
        ["tools"] = new JsonArray
        {
            Tool("device_info", "读取这台手机的型号、系统版本与应用包名。", new JsonObject()),
            Tool("list_apps", "列出手机上可启动的应用; filter 按包名或应用名做不区分大小写的子串匹配。",
                new JsonObject { ["filter"] = StringProperty("过滤词，可省略") }),
            Tool("launch_app", "按包名启动一个已安装的应用。",
                new JsonObject { ["package"] = StringProperty("Android 包名，例如 com.android.calculator2") },
                "package"),
            Tool("open_url", "用系统默认浏览器打开一个 http/https 链接。",
                new JsonObject { ["url"] = StringProperty("要打开的链接") },
                "url"),
            Tool("ui_dump", "读取当前界面节点: 编号、文本、类型与屏幕坐标; 点按优先用编号。",
                new JsonObject { ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "最多返回多少节点，默认 60" } }),
            Tool("ui_tap", "点按当前界面: 给 index 点某个节点, 或给 x/y 点坐标。",
                new JsonObject
                {
                    ["index"] = new JsonObject { ["type"] = "integer", ["description"] = "ui_dump 里的编号" },
                    ["x"] = new JsonObject { ["type"] = "integer", ["description"] = "屏幕 x" },
                    ["y"] = new JsonObject { ["type"] = "integer", ["description"] = "屏幕 y" },
                }),
            Tool("ui_swipe", "在屏幕上滑动。",
                new JsonObject
                {
                    ["from_x"] = new JsonObject { ["type"] = "integer", ["description"] = "起点 x" },
                    ["from_y"] = new JsonObject { ["type"] = "integer", ["description"] = "起点 y" },
                    ["to_x"] = new JsonObject { ["type"] = "integer", ["description"] = "终点 x" },
                    ["to_y"] = new JsonObject { ["type"] = "integer", ["description"] = "终点 y" },
                    ["duration_ms"] = new JsonObject { ["type"] = "integer", ["description"] = "滑动时长，默认 300" },
                },
                "from_x", "from_y", "to_x", "to_y"),
            Tool("ui_input", "往当前聚焦的输入框写文本; 先 ui_tap 点中输入框。",
                new JsonObject { ["text"] = StringProperty("要写入的文本") },
                "text"),
            Tool("ui_key", "按系统键: back / home / recents / notifications。",
                new JsonObject { ["action"] = StringProperty("back / home / recents / notifications") },
                "action"),
        },
    };

    private JsonObject ToolCall(JsonNode id, JsonObject? parameters)
    {
        var name = TextOf(parameters?["name"]);
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
        var result = name switch
        {
            "device_info" => PhoneActionResult.Done(device.Describe()),
            "list_apps" => ListApps(arguments),
            "launch_app" => device.LaunchApp(TextOf(arguments["package"]) ?? ""),
            "open_url" => device.OpenUrl(TextOf(arguments["url"]) ?? ""),
            "ui_dump" => UiDump(arguments),
            "ui_tap" => UiTap(arguments),
            "ui_swipe" => UiSwipe(arguments),
            "ui_input" => UiInput(arguments),
            "ui_key" => Ui().Global(TextOf(arguments["action"]) ?? "back"),
            _ => PhoneActionResult.Fail($"没有这个工具: {name}"),
        };
        return Success(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = result.Text,
                },
            },
            ["isError"] = !result.Ok,
        });
    }

    private const int DefaultDumpLimit = 60;
    private const int DefaultSwipeMillis = 300;

    private IPhoneUiAutomation Ui()
        => automation ?? throw new InvalidOperationException("无障碍服务不可用");

    private bool UiMissing()
        => automation is null || !automation.IsAvailable;

    private const string NotRunning = "无障碍服务没有开启: 到系统设置 → 无障碍里打开 DSH, 之后才能读界面、点按、输入";

    private PhoneActionResult UiDump(JsonObject arguments)
    {
        if (UiMissing())
            return PhoneActionResult.Fail(NotRunning);
        var limit = arguments["limit"] is JsonValue value && value.TryGetValue<int>(out var parsed)
            ? Math.Clamp(parsed, 1, 400)
            : DefaultDumpLimit;
        var nodes = automation!.DumpUi(limit);
        if (nodes.Count == 0)
            return PhoneActionResult.Fail("当前界面没有可读节点");
        var lines = nodes.Select(node => $"{node.Index}) {(node.Text.Length > 0 ? node.Text : $"<{node.Kind?.Split('.').LastOrDefault()}>")} [{node.Left},{node.Top}]-[{node.Right},{node.Bottom}]");
        return PhoneActionResult.Done($"前台应用: {automation.ForegroundApp}\n{string.Join('\n', lines)}");
    }

    private PhoneActionResult UiTap(JsonObject arguments)
    {
        if (UiMissing())
            return PhoneActionResult.Fail(NotRunning);
        if (arguments["index"] is JsonValue index && index.TryGetValue<int>(out var number))
            return automation!.Click(number);
        if (arguments["x"] is JsonValue x && x.TryGetValue<int>(out var px)
            && arguments["y"] is JsonValue y && y.TryGetValue<int>(out var py))
            return automation!.Tap(px, py);
        return PhoneActionResult.Fail("需要 index, 或者 x 与 y");
    }

    private PhoneActionResult UiSwipe(JsonObject arguments)
    {
        if (UiMissing())
            return PhoneActionResult.Fail(NotRunning);
        if (arguments["from_x"] is not JsonValue fromX || !fromX.TryGetValue<int>(out var x1)
            || arguments["from_y"] is not JsonValue fromY || !fromY.TryGetValue<int>(out var y1)
            || arguments["to_x"] is not JsonValue toX || !toX.TryGetValue<int>(out var x2)
            || arguments["to_y"] is not JsonValue toY || !toY.TryGetValue<int>(out var y2))
            return PhoneActionResult.Fail("需要 from_x / from_y / to_x / to_y");
        var duration = arguments["duration_ms"] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : DefaultSwipeMillis;
        return automation!.Swipe(x1, y1, x2, y2, duration);
    }

    private PhoneActionResult UiInput(JsonObject arguments)
        => UiMissing() ? PhoneActionResult.Fail(NotRunning) : automation!.SetText(TextOf(arguments["text"]) ?? "");

    private PhoneActionResult ListApps(JsonObject arguments)    {
        var apps = device.ListApps(TextOf(arguments["filter"]));
        if (apps.Count == 0)
            return PhoneActionResult.Done("没有匹配的应用。");
        var lines = apps.Select(app => $"{app.Label} · {app.Package}");
        return PhoneActionResult.Done(string.Join('\n', lines));
    }

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
            schema["required"] = new JsonArray([.. required.Select(value => JsonValue.Create(value))]);
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };
    }

    private static JsonObject StringProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    private static string? TextOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Success(JsonNode id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject Failure(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };
}
