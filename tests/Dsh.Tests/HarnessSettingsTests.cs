using Dsh.Boot;

namespace Dsh.Tests;

public sealed class HarnessSettingsTests
{
    [Fact]
    public void LoadsSettingsFromHarnessHome()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "settings-test-home", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "settings.yaml");
        File.WriteAllText(path, """
            provider: custom-provider
            model: custom-model
            baseUrl: http://127.0.0.1:11434/v1
            apiKey: sk-test
            reasoningEffort: max
            """);
        try
        {
            var settings = HarnessSettings.Load(new HarnessHome(home));
            Assert.Equal("custom-provider", settings.Provider);
            Assert.Equal("custom-model", settings.Model);
            Assert.Equal("http://127.0.0.1:11434/v1", settings.BaseUrl);
            Assert.Equal("sk-test", settings.ApiKey);
            Assert.Equal("max", settings.ReasoningEffort);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }
}