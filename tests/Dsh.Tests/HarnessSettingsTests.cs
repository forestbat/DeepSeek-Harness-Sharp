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
    public void LoadTemplateFallsBackToMinimalTemplateWhenFileMissing()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-settings-fallback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "settings.yaml");
        var missingBase = Path.Combine(Path.GetTempPath(), "dsh-no-templates", Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, HarnessSettings.LoadTemplate(missingBase));
            var settings = HarnessSettings.Load(new HarnessHome(home));
            Assert.Equal("deepseek-official/deepseek-v4-flash", settings.GlobalDefaultModel);
            Assert.Equal("deepseek-official/deepseek-v4-flash", settings.CompactionModel);
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

    [Fact]
    public void LoadsPluginsFromBooleanAndMappingForms()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-settings-plugins", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "settings.yaml");
        File.WriteAllText(path, """
            plugins:
              "@scope/off": false
              "@scope/on": true
              "@scope/configured":
                enabled: true
                maxOutputChars: 16000
                sample: false
              "@scope/configured-off":
                enabled: false
                mode: quiet
            """);
        try
        {
            var settings = HarnessSettings.Load(new HarnessHome(home));
            Assert.False(settings.Plugins["@scope/off"].Enabled);
            Assert.True(settings.Plugins["@scope/on"].Enabled);
            var configured = settings.Plugins["@scope/configured"];
            Assert.True(configured.Enabled);
            Assert.Equal(16000L, configured.Parameters["maxOutputChars"]);
            Assert.Equal(false, configured.Parameters["sample"]);
            Assert.False(settings.Plugins["@scope/configured-off"].Enabled);
            Assert.Equal("quiet", settings.Plugins["@scope/configured-off"].Parameters["mode"]);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void SavePlugins_ReplacesBlockAndKeepsComments()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-settings-save", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "settings.yaml");
        File.WriteAllText(path, """
            # 顶部注释
            rules: []

            # 插件段之前的注释
            plugins: {}

            # 插件段之后的注释
            safety:
              autoApprove: false
            """);
        try
        {
            var settings = HarnessSettings.Load(new HarnessHome(home));
            settings.Plugins["@scope/off"] = new PluginSetting { Enabled = false };
            settings.Plugins["@scope/configured"] = new PluginSetting
            {
                Enabled = true,
                Parameters = new Dictionary<string, object?> { ["maxOutputChars"] = 16000L },
            };
            settings.SavePlugins(new HarnessHome(home));

            var text = File.ReadAllText(path);
            Assert.Contains("\"@scope/off\": false", text);
            Assert.Contains("\"@scope/configured\":", text);
            Assert.Contains("maxOutputChars: 16000", text);
            Assert.Contains("# 顶部注释", text);
            Assert.Contains("# 插件段之后的注释", text);
            Assert.Contains("safety:", text);
            Assert.DoesNotContain("plugins: {}", text);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void Save_KeepsPluginsBlockWhenWritingOtherSections()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-settings-save2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "settings.yaml");
        try
        {
            HarnessSettings.Load(new HarnessHome(home));
            var settings = HarnessSettings.Load(new HarnessHome(home));
            settings.Rules.Add("keep-me");
            settings.Save(new HarnessHome(home));

            var text = File.ReadAllText(path);
            Assert.Contains("plugins:", text);
            Assert.Contains("keep-me", text);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }
}
