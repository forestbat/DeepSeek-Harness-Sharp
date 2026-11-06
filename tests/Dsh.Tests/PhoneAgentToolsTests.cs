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
        using var registration = PhoneAgentTools.Register(ctx, phone);

        var names = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Schemas()
            .Select(schema => schema.Name)
            .Where(name => name.StartsWith("android_", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(
            [PhoneAgentTools.DeviceInfoName, PhoneAgentTools.LauncherName, PhoneAgentTools.ListAppsName, PhoneAgentTools.OpenUrlName],
            names);
    }

    [Fact]
    public async Task LaunchAppHandsTheDeviceAnswerToTheModel()
    {
        var (ctx, tools, phone) = Harness();
        using var registration = PhoneAgentTools.Register(ctx, phone);

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
        using var registration = PhoneAgentTools.Register(ctx, phone);

        var result = await Execute(tools, PhoneAgentTools.LauncherName, """{"package":"com.nope.zzz"}""");

        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Contains("没有可启动的应用 com.nope.zzz", Assert.IsType<TextBlock>(Assert.Single(success.Content)).Text, StringComparison.Ordinal);
        Assert.False(success.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ListAppsPrintsOneLinePerApp()
    {
        var (ctx, tools, phone) = Harness();
        using var registration = PhoneAgentTools.Register(ctx, phone);

        var result = await Execute(tools, PhoneAgentTools.ListAppsName, "{}");

        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Equal($"计算器 · com.android.calculator2\nChrome · com.android.chrome",
            Assert.IsType<TextBlock>(Assert.Single(success.Content)).Text);
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
}
