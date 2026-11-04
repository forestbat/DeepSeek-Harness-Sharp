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
public sealed class PhoneMcpProtocol(IPhoneDevice device)
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

    private PhoneActionResult ListApps(JsonObject arguments)
    {
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
