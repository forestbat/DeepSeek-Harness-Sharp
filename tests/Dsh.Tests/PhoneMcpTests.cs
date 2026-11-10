using System.Text;
using System.Text.Json.Nodes;
using Dsh.Gui.Shared.Mobile;

namespace Dsh.Tests;

/** 手机工具的 MCP 协议层: 工具形状、调用结果与 HTTP 令牌都在桌面上就能验完。 */
public sealed class PhoneMcpTests
{
    private const string Token = "0123456789abcdef";

    [Fact]
    public void InitializeEchoesProtocolVersionAndAdvertisesTools()
    {
        var protocol = new PhoneMcpProtocol(new FakePhone());

        var reply = protocol.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""").GetAwaiter().GetResult();

        var result = Result(reply);
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Equal(PhoneMcpProtocol.ServerName, result["serverInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsListDescribesPhoneCapabilities()
    {
        var protocol = new PhoneMcpProtocol(new FakePhone());

        var tools = Result(protocol.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""").GetAwaiter().GetResult())["tools"]!.AsArray();

        var names = tools.Select(tool => tool!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("launch_app", names);
        Assert.Contains("list_apps", names);
        Assert.Contains("open_url", names);
        Assert.Contains("device_info", names);
        var launch = tools.Single(tool => tool!["name"]!.GetValue<string>() == "launch_app")!;
        Assert.Equal("package", launch["inputSchema"]!["required"]![0]!.GetValue<string>());
    }

    [Fact]
    public void LaunchAppCallsTheDeviceAndReportsFailureAsToolError()
    {
        var phone = new FakePhone();
        var protocol = new PhoneMcpProtocol(phone);

        var ok = Call(protocol, "launch_app", """{"package":"com.android.calculator2"}""");
        Assert.False(ok["isError"]!.GetValue<bool>());
        Assert.Equal(["com.android.calculator2"], phone.Launched);

        var failed = Call(protocol, "launch_app", """{"package":"no.such.app"}""");
        Assert.True(failed["isError"]!.GetValue<bool>());
        Assert.Contains("no.such.app", failed["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownToolIsReportedWithoutBreakingTheSession()
    {
        var protocol = new PhoneMcpProtocol(new FakePhone());

        var result = Call(protocol, "rm_rf", "{}");

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("rm_rf", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void NotificationHasNoResponseBody() =>
        Assert.Null(new PhoneMcpProtocol(new FakePhone())
            .HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""").GetAwaiter().GetResult());

    [Fact]
    public async Task HttpEndpointAcceptsTheTokenInThePath()
    {
        var reply = await PostAsync($"/mcp/{Token}", """{"jsonrpc":"2.0","id":7,"method":"ping"}""");

        Assert.StartsWith("HTTP/1.1 200", reply);
        Assert.Contains("\"id\":7", reply);
    }

    [Fact]
    public async Task HttpEndpointRejectsAnotherToken()
    {
        var reply = await PostAsync("/mcp/wrong", """{"jsonrpc":"2.0","id":7,"method":"ping"}""");

        Assert.StartsWith("HTTP/1.1 401", reply);
    }

    [Fact]
    public async Task HealthCheckServesWithoutToken()
    {
        var scene = new HttpScene();
        await scene.SendAsync("GET /health HTTP/1.1\r\nHost: phone\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200", scene.Response);
    }

    [Fact]
    public async Task NotificationPostAnswersAcceptedWithoutBody()
    {
        var reply = await PostAsync($"/mcp/{Token}", """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.StartsWith("HTTP/1.1 202", reply);
    }

    private static async Task<string> PostAsync(string path, string body)
    {
        var scene = new HttpScene();
        var payload = Encoding.UTF8.GetBytes(body);
        await scene.SendAsync(
            $"POST {path} HTTP/1.1\r\nHost: phone\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\n\r\n{body}");
        return scene.Response;
    }

    private static JsonObject Call(PhoneMcpProtocol protocol, string name, string arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = JsonNode.Parse(arguments),
            },
        };
        return Result(protocol.HandleAsync(request.ToJsonString()).GetAwaiter().GetResult());
    }

    private static JsonObject Result(string? reply)
    {
        Assert.NotNull(reply);
        var node = JsonNode.Parse(reply)!;
        return node["result"]!.AsObject();
    }

    private sealed class HttpScene
    {
        private readonly MemoryStream _output = new();

        public string Response => Encoding.UTF8.GetString(_output.ToArray());

        public async Task SendAsync(string request)
        {
            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(request));
            var http = new PhoneMcpHttp(new PhoneMcpProtocol(new FakePhone()), Token);
            await http.ServeOnceAsync(input, _output, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FakePhone : IPhoneDevice
    {
        public List<string> Launched { get; } = [];

        public string Describe() => "Fake Phone · Android 14";

        public IReadOnlyList<PhoneApp> ListApps(string? filter) =>
        [
            new PhoneApp("com.android.calculator2", "计算器"),
            new PhoneApp("com.android.chrome", "Chrome"),
        ];

        public PhoneActionResult LaunchApp(string package)
        {
            Launched.Add(package);
            return package == "com.android.calculator2"
                ? PhoneActionResult.Done($"已启动 {package}")
                : PhoneActionResult.Fail($"没有可启动的应用 {package}");
        }

        public PhoneActionResult OpenUrl(string url) => PhoneActionResult.Done($"已打开 {url}");
    }
}
