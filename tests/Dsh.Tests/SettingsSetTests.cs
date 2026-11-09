using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using static Dsh.Inspection.Plugin;

namespace Dsh.Tests;

/** settings_set: 结构化写 settings.yaml —— 只动指定的那一条, 注释与其它段逐字节保留, 未知路径拒绝。 */
public sealed class SettingsSetTests
{
    private const string Initial = """
        # 用户注释: 保留
        global_default_model: test-provider/test-model
        mcp:
          phone:
            url: "http://old"
            enabled: true
        """;

    [Fact]
    public async Task Writes_One_Key_And_Keeps_Everything_Else()
    {
        using var fixture = new Fixture();
        await fixture.ComposeAsync();

        var result = await fixture.Set("""{"path":"mcp.phone.url","value":"http://new"}""");

        _ = Ok(result);
        var written = File.ReadAllText(fixture.SettingsPath);
        Assert.Contains("url: \"http://new\"", written);
        Assert.Contains("# 用户注释: 保留", written);
        Assert.Contains("enabled: true", written);
        Assert.Contains("global_default_model: test-provider/test-model", written);
    }

    /** 装配类段的改动要明确告诉模型"下次启动或 reload 才生效", 否则它以为已经改了行为。 */
    [Fact]
    public async Task Reports_When_The_Change_Takes_Effect()
    {
        using var fixture = new Fixture();
        await fixture.ComposeAsync();

        var immediate = await fixture.Set("""{"path":"logging.level","value":"debug"}""");
        var restart = await fixture.Set("""{"path":"mcp.phone.url","value":"http://new"}""");

        Assert.Contains("next settings read", Result(immediate));
        Assert.Contains("after restart", Result(restart));
    }

    [Fact]
    public async Task Delete_Removes_The_Key()
    {
        using var fixture = new Fixture();
        await fixture.ComposeAsync();

        var result = await fixture.Set("""{"path":"mcp.phone.enabled","mode":"delete"}""");

        _ = Ok(result);
        Assert.DoesNotContain("enabled", File.ReadAllText(fixture.SettingsPath));
        Assert.Contains("url: \"http://old\"", File.ReadAllText(fixture.SettingsPath));
    }

    /** 拼错的键写进去会被启动静默忽略, 所以在写之前就拒绝, 并给出最近的可用键。 */
    [Fact]
    public async Task Unknown_Path_Is_Rejected_Without_Touching_The_File()
    {
        using var fixture = new Fixture();
        await fixture.ComposeAsync();
        var before = File.ReadAllText(fixture.SettingsPath);

        var result = await fixture.Set("""{"path":"globel_default_model","value":"x"}""");

        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Contains("unknown settings path", failure.Error.Message);
        Assert.Equal(before, File.ReadAllText(fixture.SettingsPath));
    }

    private static ToolExecutionResult.Success Ok(ToolExecutionResult result)
        => result as ToolExecutionResult.Success
           ?? throw new Xunit.Sdk.XunitException(
               $"settings_set failed: {string.Join(' ', result.Content.OfType<TextBlock>().Select(block => block.Text))}");

    private static string Result(ToolExecutionResult result)
        => Ok(result).Value.GetProperty("result").GetString() ?? "";

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dsh-set-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            SettingsPath = Path.Combine(Root, "settings.yaml");
            File.WriteAllText(SettingsPath, Initial.ReplaceLineEndings("\n") + "\n");
            Ctx = new Context();
            Ctx.SetOwn("dshHomePath", Root);
            Ctx.SetOwn("harnessOptions", new HarnessOptions(HarnessHome.Resolve(Root), Root));
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Ctx.Provide("approval", new AllowAll());
        }

        public string Root { get; }
        public string SettingsPath { get; }
        public Context Ctx { get; }
        public ToolRuntime Tools { get; }

        public async Task ComposeAsync()
        {
            _ = Ctx.Plugin(InspectionTestPlugin.Definition(PluginManager));
            await Ctx.Scheduler.SettleAsync();
        }

        public Task<ToolExecutionResult> Set(string arguments)
            => Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = Dsh.Inspection.ToolSettings.ToolName,
                Arguments = JsonDocument.Parse(arguments).RootElement,
                Agent = InspectionTestAgent.Open(Ctx),
                Signal = default,
            });

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }

        private sealed class AllowAll : IApprovalService
        {
            public Task<ApprovalOutcome> Request(ApprovalRequest request, CancellationToken signal)
                => Task.FromResult(ApprovalOutcome.AllowedOnce);
            public bool IsDenied(IAgent agent) => false;
        }
    }
}
