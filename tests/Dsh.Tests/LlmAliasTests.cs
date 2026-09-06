using Dsh.Boot;
using Dsh.Core;

namespace Dsh.Tests;

public sealed class LlmAliasTests
{
    [Fact]
    public void ComposeRegistersConfiguredProviderAlias()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "llm-alias-test-home", Guid.NewGuid().ToString("N"));
        using (var app = HarnessComposer.Compose(new HarnessOptions(
            new HarnessHome(home),
            Directory.GetCurrentDirectory(),
            Provider: "openai-compatible",
            BaseUrl: "http://127.0.0.1",
            ApiKey: "sk-test")))
        {
            var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
            var models = llm.ListModels("openai-compatible");
            Assert.Contains(models, model => model.Id == "deepseek-v4-flash");
        }
        Directory.Delete(home, true);
    }
}