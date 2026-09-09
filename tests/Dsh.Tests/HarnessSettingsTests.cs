using Dsh.Boot;

namespace Dsh.Tests;

public sealed class HarnessSettingsTests
{
    [Fact]
    public void LoadsV2SettingsFromHarnessHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-settings-v2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "settings.yaml");
        File.WriteAllText(path, """
            global_default_model: custom/provider-model
            compaction_model: custom/compact-model
            subagent:
              default_model: custom/sub-model
            providers:
              custom:
                type: openai-compatible
                options:
                  baseUrl: http://127.0.0.1:11434/v1
                  apiKey: sk-test
                models:
                  custom-model:
                    name: Custom Model
                    reasoning: true
                    tool_call: true
            skills:
              paths: [a]
              urls: [b]
            rules: [rule1]
            mcp:
              filesystem:
                transport: stdio
                command: ["npx", "-y", "server"]
                args: []
                url: null
                enabled: true
            compaction:
              auto: false
              prune: true
            safety:
              autoApprove: true
              blacklist: []
            """);
        try
        {
            var settings = HarnessSettings.Load(new HarnessHome(home));
            Assert.Equal("custom/provider-model", settings.GlobalDefaultModel);
            Assert.Equal("custom/compact-model", settings.CompactionModel);
            Assert.Equal("custom/sub-model", settings.Subagent?.DefaultModel);
            Assert.Equal("custom", Assert.Single(settings.Providers).Key);
            var provider = settings.Providers["custom"];
            Assert.Equal("openai-compatible", provider.Type);
            Assert.Equal("http://127.0.0.1:11434/v1", provider.Options?.BaseUrl);
            Assert.Equal("sk-test", provider.Options?.ApiKey);
            var model = Assert.Single(provider.Models);
            Assert.Equal("custom-model", model.Key);
            Assert.Equal("Custom Model", model.Value.Name);
            Assert.True(model.Value.Reasoning);
            Assert.True(model.Value.ToolCall);
            Assert.Equal(["a"], settings.Skills?.Paths);
            Assert.Equal(["b"], settings.Skills?.Urls);
            Assert.Equal(["rule1"], settings.Rules);
            var mcp = Assert.Single(settings.McpServers);
            Assert.Equal("filesystem", mcp.Key);
            Assert.Equal(["npx", "-y", "server"], mcp.Value.Command);
            Assert.True(mcp.Value.Enabled);
            Assert.False(settings.Compaction?.Auto);
            Assert.True(settings.Compaction?.Prune);
            Assert.True(settings.Safety?.AutoApprove);
            Assert.Empty(settings.Safety?.Blacklist ?? []);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void LoadCreatesCommentedTemplateWhenSettingsMissing()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-settings-template", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "settings.yaml");
        try
        {
            Assert.False(File.Exists(path));
            var settings = HarnessSettings.Load(new HarnessHome(home));
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.Contains("# DeepSeek Harness 配置文件", content);
            Assert.Contains("global_default_model: deepseek-official/deepseek-v4-flash", content);
            Assert.Contains("compaction_model: deepseek-official/deepseek-v4-flash", content);
            Assert.Contains("subagent:", content);
            Assert.Contains("providers:", content);
            Assert.Contains("mcp:", content);
            Assert.Equal("deepseek-official/deepseek-v4-flash", settings.GlobalDefaultModel);
            Assert.NotNull(settings.Providers["deepseek-official"].Options?.BaseUrl);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void ResolvesDefaultModelFromProviderModelString()
    {
        var settings = new HarnessSettings
        {
            GlobalDefaultModel = "provider-a/model-b",
        };
        var resolved = settings.ResolveDefaultModel();
        Assert.NotNull(resolved);
        Assert.Equal(("provider-a", "model-b"), resolved);
    }
}
