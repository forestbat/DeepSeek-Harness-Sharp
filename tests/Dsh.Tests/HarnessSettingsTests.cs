using Dsh.Boot;

namespace Dsh.Tests;

public sealed class HarnessSettingsTests
{
    [Fact]
    public void LoadsMultiConfigSettingsFromHarnessHome()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "settings-test-home", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "settings.yaml");
        File.WriteAllText(path, """
            default: work
            providers:
              custom:
                type: openai-compatible
                baseUrl: http://127.0.0.1:11434/v1
                apiKey: sk-test
            configs:
              work:
                provider: custom
                model: custom-model
                reasoningEffort: max
            safety:
              autoApprove: true
              blacklist: []
            """);
        try
        {
            var settings = HarnessSettings.Load(new HarnessHome(home));
            Assert.Equal("work", settings.Default);
            Assert.Equal("custom", Assert.Single(settings.Providers).Key);
            var provider = settings.Providers["custom"];
            Assert.Equal("openai-compatible", provider.Type);
            Assert.Equal("http://127.0.0.1:11434/v1", provider.BaseUrl);
            Assert.Equal("sk-test", provider.ApiKey);
            var config = settings.ResolveConfig("work");
            Assert.NotNull(config);
            Assert.Equal("custom", config.Provider);
            Assert.Equal("custom-model", config.Model);
            Assert.Equal("max", config.ReasoningEffort);
            Assert.True(settings.Safety?.AutoApprove);
            Assert.Empty(settings.Safety?.Blacklist ?? []);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void ResolvesDefaultConfigWhenNameIsNull()
    {
        var settings = new HarnessSettings
        {
            Default = "fast",
            Configs = new()
            {
                ["work"] = new ConfigSettings { Provider = "a", Model = "m1" },
                ["fast"] = new ConfigSettings { Provider = "b", Model = "m2" },
            },
        };
        var resolved = settings.ResolveConfig();
        Assert.Equal("b", resolved?.Provider);
        Assert.Equal("m2", resolved?.Model);
    }
}
