using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using static Dsh.Inspection.Plugin;

namespace Dsh.Tests;

/**
 * 守卫不认工具名: 任何一个工具跑完, 只要 settings.yaml 变了就校验一次。
 * 坏文本在同一轮工具调用内被还原, 原文隔离, 结果改成失败让模型立刻看到。
 */
public sealed class SettingsFileGuardTests
{
    private const string Good = "global_default_model: test-provider/test-model\n";

    private const string Written = """
        global_default_model: test-provider/test-model
        mcp:
          phone:
            url: "http://x"
              enabled: true
        """;

    [Fact]
    public async Task Broken_Write_Is_Undone_And_Reported_To_The_Model()
    {
        using var fixture = new Fixture();
        await fixture.ComposeAsync();

        var result = await fixture.WriteToSettings(Written);

        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        var text = string.Concat(failure.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.Contains("已被撤销", text);
        Assert.Contains("settings_set", text);
        Assert.Equal(Good, File.ReadAllText(fixture.SettingsPath));
        Assert.Single(Directory.GetFiles(fixture.Root, "settings.yaml.invalid-*"));
    }

    [Fact]
    public async Task Valid_Write_Passes_Through()
    {
        using var fixture = new Fixture();
        await fixture.ComposeAsync();

        var result = await fixture.WriteToSettings(Good + "logging:\n  level: debug\n");

        Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Contains("level: debug", File.ReadAllText(fixture.SettingsPath));
        Assert.Empty(Directory.GetFiles(fixture.Root, "settings.yaml.invalid-*"));
    }

    [Fact]
    public async Task Unrelated_Writes_Are_Ignored()
    {
        using var fixture = new Fixture();
        await fixture.ComposeAsync();

        var result = await fixture.Write("other.txt", "not yaml at all: [");

        Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Equal(Good, File.ReadAllText(fixture.SettingsPath));
    }

    /** 守卫本身不认识工具/通知: 直接 Capture + Check 就能用, 工具通知只是触发者之一。 */
    [Fact]
    public void Guard_Alone_Restores_A_Broken_Write()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-guard-alone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "settings.yaml");
            File.WriteAllText(settingsPath, Good);
            var guard = new SettingsFileGuard(HarnessHome.Resolve(root));
            guard.Accept();
            guard.Capture();

            File.WriteAllText(settingsPath, Written);
            var message = guard.Check();

            Assert.NotNull(message);
            Assert.Contains("已被撤销", message);
            Assert.Equal(Good, File.ReadAllText(settingsPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dsh-guard-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            SettingsPath = Path.Combine(Root, "settings.yaml");
            File.WriteAllText(SettingsPath, Good);
            Ctx = new Context();
            Ctx.SetOwn("dshHomePath", Root);
            Ctx.SetOwn("harnessOptions", new HarnessOptions(HarnessHome.Resolve(Root), Root));
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Ctx.Provide("approval", new AllowAll());
            Tools.Register(WriterTool());
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

        public Task<ToolExecutionResult> WriteToSettings(string content) => Write("settings.yaml", content);

        public Task<ToolExecutionResult> Write(string name, string content)
            => Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = WriterName,
                Arguments = JsonDocument.Parse($$"""{"name":"{{name}}","content":{{JsonSerializer.Serialize(content)}}}""").RootElement,
                Agent = InspectionTestAgent.Open(Ctx),
                Signal = default,
            });

        private const string WriterName = "test_file_writer";

        private ToolDefinition WriterTool() => new()
        {
            Name = WriterName,
            Description = "Writes a file next to settings.yaml; stands in for any tool with write access (MCP, plugin, bash).",
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "required": ["name", "content"],
                  "properties": {
                    "name": { "type": "string" },
                    "content": { "type": "string" }
                  }
                }
                """)!.AsObject(),
            Output = new ToolOutputDefinition(
                JsonNode.Parse("""{"type":"object"}""")!.AsObject(),
                (_, _) => [new TextBlock("written")]),
            Execute = (args, _) =>
            {
                var name = args.GetProperty("name").GetString() ?? "";
                File.WriteAllText(Path.Combine(Root, name), args.GetProperty("content").GetString() ?? "");
                return Task.FromResult<object?>(new JsonObject());
            },
        };

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
