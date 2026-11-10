using System.Text.Json;
using Dsh.Core;
using Dsh.Gui.Shared.Mobile;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/** 手机原生工具要同时满足两件事: 结果文本进得去模型, 输出的 JSON 形状与声明的 schema 一致。 */
public sealed class PhoneAgentToolsTests
{
    [Fact]
    public void RegistersEveryPhoneCapability()
    {
        var (ctx, _, phone) = Harness();
        using var registration = PhoneAgentTools.Register(ctx, phone, new FakeAutomation());

        var names = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Schemas()
            .Select(schema => schema.Name)
            .Where(name => name.StartsWith("android_", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

        string[] expected =
        [
            PhoneAgentTools.DeviceInfoName, PhoneAgentTools.DumpUiName, PhoneAgentTools.InputName,
            PhoneAgentTools.KeyName, PhoneAgentTools.LauncherName, PhoneAgentTools.ListAppsName,
            PhoneAgentTools.LongPressName, PhoneAgentTools.OpenUrlName, PhoneAgentTools.ScrollName,
            PhoneAgentTools.ScreenshotName, PhoneAgentTools.SwipeName, PhoneAgentTools.TapName,
            PhoneAgentTools.WaitForName,
        ];
        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task LaunchAppHandsTheDeviceAnswerToTheModel()
    {
        var (ctx, tools, phone) = Harness();
        using var registration = PhoneAgentTools.Register(ctx, phone, new FakeAutomation());

        var result = await Execute(tools, PhoneAgentTools.LauncherName, """{"package":"com.android.calculator2"}""");

        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Equal("已启动 com.android.calculator2", Assert.IsType<TextBlock>(Assert.Single(success.Content)).Text);
        Assert.Equal(["com.android.calculator2"], phone.Launched);
        Assert.Equal("已启动 com.android.calculator2", success.Value.GetProperty("text").GetString());
        Assert.True(success.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task FailedLaunchKeepsTheReasonInsideTheText()
    {
        var (ctx, tools, phone) = Harness();
        using var registration = PhoneAgentTools.Register(ctx, phone, new FakeAutomation());

        var result = await Execute(tools, PhoneAgentTools.LauncherName, """{"package":"com.nope.zzz"}""");

        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Contains("没有可启动的应用 com.nope.zzz", Assert.IsType<TextBlock>(Assert.Single(success.Content)).Text, StringComparison.Ordinal);
        Assert.False(success.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ListAppsPrintsOneLinePerApp()
    {
        var (ctx, tools, phone) = Harness();
        using var registration = PhoneAgentTools.Register(ctx, phone, new FakeAutomation());

        var result = await Execute(tools, PhoneAgentTools.ListAppsName, "{}");

        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Equal($"计算器 · com.android.calculator2\nChrome · com.android.chrome",
            Assert.IsType<TextBlock>(Assert.Single(success.Content)).Text);
    }

    [Fact]
    public async Task DumpUiListsNodesWithIndexAndBounds()
    {
        var (ctx, tools, phone) = Harness();
        var automation = new FakeAutomation();
        using var registration = PhoneAgentTools.Register(ctx, phone, automation);

        var result = await Execute(tools, PhoneAgentTools.DumpUiName, "{}");

        var text = Assert.IsType<TextBlock>(Assert.Single(Assert.IsType<ToolExecutionResult.Success>(result).Content)).Text;
        Assert.Contains("前台应用: com.android.calculator2", text, StringComparison.Ordinal);
        Assert.Contains("0) 7（可点） [0,0]-[100,100] com.android.calculator2:id/digit_7", text, StringComparison.Ordinal);
        Assert.Contains("1) 加号（可点） [100,0]-[200,100]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DumpUiSaysSoWhenAccessibilityIsOff()
    {
        var (ctx, tools, phone) = Harness();
        using var registration = PhoneAgentTools.Register(ctx, phone, new FakeAutomation { IsAvailable = false });

        var result = await Execute(tools, PhoneAgentTools.DumpUiName, "{}");

        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.False(success.Value.GetProperty("ok").GetBoolean());
        Assert.Contains("无障碍服务没有开启", Assert.IsType<TextBlock>(Assert.Single(success.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TapPrefersNodeIndexAndFallsBackToCoordinates()
    {
        var (ctx, tools, phone) = Harness();
        var automation = new FakeAutomation();
        using var registration = PhoneAgentTools.Register(ctx, phone, automation);

        await Execute(tools, PhoneAgentTools.TapName, """{"index":3}""");
        await Execute(tools, PhoneAgentTools.TapName, """{"x":10,"y":20}""");

        Assert.Equal(["click 3", "tap 10,20"], automation.Actions);
    }

    [Fact]
    public async Task InputAndKeyReachTheAutomation()
    {
        var (ctx, tools, phone) = Harness();
        var automation = new FakeAutomation();
        using var registration = PhoneAgentTools.Register(ctx, phone, automation);

        await Execute(tools, PhoneAgentTools.InputName, """{"text":"7+8"}""");
        await Execute(tools, PhoneAgentTools.KeyName, """{"action":"back"}""");

        Assert.Equal(["input 7+8", "key back"], automation.Actions);
    }

    private static (Context Ctx, ToolRuntime Tools, FakePhone Phone) Harness()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        return (ctx, new ToolRuntime(ctx), new FakePhone());
    }

    private static Task<ToolExecutionResult> Execute(ToolRuntime tools, string name, string argumentsJson)
        => tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = JsonDocument.Parse(argumentsJson).RootElement,
            Signal = default,
        });

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
            return package.StartsWith("com.android.", StringComparison.Ordinal)
                ? PhoneActionResult.Done($"已启动 {package}")
                : PhoneActionResult.Fail($"没有可启动的应用 {package}");
        }

        public PhoneActionResult OpenUrl(string url) => PhoneActionResult.Done($"已打开 {url}");
    }

    /** 假的无障碍层: 记录被点/被滑/被输入的动作, 让工具参数解析与回执可断言。 */
    private sealed class FakeAutomation : IPhoneUiAutomation
    {
        public bool IsAvailable { get; set; } = true;
        public string ForegroundApp => "com.android.calculator2";
        public List<string> Actions { get; } = [];

        public IReadOnlyList<PhoneUiNode> DumpUi(int limit) =>
        [
            new PhoneUiNode(0, "7", null, "android.widget.Button", "com.android.calculator2:id/digit_7", true, false, false, false, false, 0, 0, 100, 100),
            new PhoneUiNode(1, "", "加号", "android.widget.Button", null, true, false, false, false, false, 100, 0, 200, 100),
        ];

        public PhoneActionResult Tap(int x, int y)
        {
            Actions.Add($"tap {x},{y}");
            return PhoneActionResult.Done($"已点击 ({x},{y})");
        }

        public PhoneActionResult LongPress(int x, int y, int durationMs)
        {
            Actions.Add($"longpress {x},{y} {durationMs}");
            return PhoneActionResult.Done($"已长按 ({x},{y})");
        }

        public PhoneActionResult Swipe(int fromX, int fromY, int toX, int toY, int durationMs)
        {
            Actions.Add($"swipe {fromX},{fromY}->{toX},{toY} {durationMs}");
            return PhoneActionResult.Done("已滑动");
        }

        public PhoneActionResult Scroll(string direction, int times)
        {
            Actions.Add($"scroll {direction} {times}");
            return PhoneActionResult.Done($"已向 {direction} 滚动 {times} 屏");
        }

        public Task<PhoneActionResult> WaitForAsync(string needle, int timeoutMs, CancellationToken cancellationToken)
        {
            Actions.Add($"wait {needle} {timeoutMs}");
            return Task.FromResult(PhoneActionResult.Done($"界面上出现了「{needle}」"));
        }

        public Task<PhoneCapture> CaptureAsync(CancellationToken cancellationToken)
        {
            Actions.Add("screenshot");
            return Task.FromResult(new PhoneCapture(true, "截图 1080×2400", new PhoneScreenshot([1, 2, 3], 1080, 2400)));
        }

        public PhoneActionResult Click(int index)
        {
            Actions.Add($"click {index}");
            return PhoneActionResult.Done($"已点击节点 {index}");
        }

        public PhoneActionResult SetText(string text)
        {
            Actions.Add($"input {text}");
            return PhoneActionResult.Done($"已输入 {text.Length} 个字符");
        }

        public PhoneActionResult Global(string action)
        {
            Actions.Add($"key {action}");
            return PhoneActionResult.Done($"已执行 {action}");
        }
    }
}
