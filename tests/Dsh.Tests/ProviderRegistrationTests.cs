using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tests;

/** provider 注册的真实链路:settings.yaml → 按 type 匹配适配器插件 → 缺 key/未知类型跳过;
 *  CLI 指定的 provider 走缺省 wire,零配置不兜底。 */
public sealed class ProviderRegistrationTests
{
    [Fact]
    public async Task Compose_SkipsProviderWithoutCredential()
    {
        using var home = new TestHome("""
            global_default_model: my-provider/some-model
            providers:
              my-provider:
                type: openai-compatible
                options:
                  baseUrl: https://example.invalid
                  apiKeyEnv: DSH_TEST_MISSING_KEY
            """);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root));

        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
        Assert.Empty(llm.ListProviders());
        Assert.Contains(app.Ctx.Root.Logger.Buffer, message => message.Text.Contains("skipped: API key is not configured"));
        Assert.Equal("my-provider", app.Provider);
    }

    [Fact]
    public async Task Compose_RegistersProviderWhenCredentialIsConfigured()
    {
        using var home = new TestHome("""
            global_default_model: my-provider/some-model
            providers:
              my-provider:
                type: openai-compatible
                options:
                  baseUrl: https://example.invalid
                  apiKey: sk-test
            """);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root));

        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
        var provider = Assert.Single(llm.ListProviders());
        Assert.Equal("my-provider", provider.Id);
        Assert.DoesNotContain(app.Ctx.Root.Logger.Buffer, message => message.Text.Contains("skipped: API key is not configured"));
    }

    [Fact]
    public async Task Compose_UnknownTypeWarnsAndSkips()
    {
        using var home = new TestHome("""
            providers:
              weird:
                type: no-such-wire
                options:
                  baseUrl: https://example.invalid
                  apiKey: sk-test
            """);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root));

        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
        Assert.Empty(llm.ListProviders());
        Assert.Contains(app.Ctx.Root.Logger.Buffer,
            message => message.Text.Contains("no adapter plugin serves type \"no-such-wire\""));
    }

    [Fact]
    public async Task Compose_MissingTypeDefaultsToOpenAiCompatible()
    {
        using var home = new TestHome("""
            providers:
              no-type:
                options:
                  baseUrl: https://example.invalid
                  apiKey: sk-test
            """);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root));

        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
        Assert.Equal("no-type", Assert.Single(llm.ListProviders()).Id);
        Assert.DoesNotContain(app.Ctx.Root.Logger.Buffer, message => message.Text.Contains("no adapter plugin serves type"));
    }

    [Fact]
    public async Task Compose_ExplicitDeepSeekTypeUsesDeepSeekCatalog()
    {
        using var home = new TestHome("""
            providers:
              ds:
                type: deepseek
                options:
                  apiKey: sk-test
            """);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root));

        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
        Assert.Equal("ds", Assert.Single(llm.ListProviders()).Id);
        Assert.Contains(llm.ListModels("ds"), model => model.Id == "deepseek-v4-flash");
    }

    [Fact]
    public async Task Compose_CliProviderUsesDefaultWireWithoutDeepSeekPrivilege()
    {
        using var home = new TestHome("global_default_model: cli-provider/some-model\n");
        using var app = await HarnessComposer.Compose(new HarnessOptions(
            home.Home,
            home.Root,
            Provider: "cli-provider",
            BaseUrl: "https://example.invalid",
            ApiKey: "sk-test"));

        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
        var provider = Assert.Single(llm.ListProviders());
        Assert.Equal("cli-provider", provider.Id);
        Assert.Equal("OpenAI-Compatible", provider.Name);
    }

    [Fact]
    public async Task Compose_NoProvidersStartsAndFailsOnlyAtCallTime()
    {
        using var home = new TestHome("plugins: {}\n");
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root));

        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
        Assert.Empty(llm.ListProviders());
        Assert.Equal("", app.Provider);
        var failure = await Assert.ThrowsAsync<LlmException>(() => llm.PrepareCall(new LlmCallConfig("", ""), TestContext.Current.CancellationToken));
        Assert.Equal(LlmFailureCodes.NoAdapter, failure.Failure.Code);
        Assert.Contains("no LLM provider is configured", failure.Failure.Message);
    }

    private sealed class TestHome : IDisposable
    {
        public TestHome(string settings)
        {
            Root = Path.Combine(Path.GetTempPath(), $"dsh-provider-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "settings.yaml"), settings);
            Home = new HarnessHome(Root);
        }

        public string Root { get; }

        public HarnessHome Home { get; }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
