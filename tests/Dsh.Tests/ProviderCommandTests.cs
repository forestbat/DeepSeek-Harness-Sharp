using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

public sealed class ProviderCommandTests
{
    [Fact]
    public async Task ProviderAdd_FirstModelSetsGlobalDefault()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-provider-cmd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            File.WriteAllText(Path.Combine(home, "settings.yaml"), """
                global_default_model: null
                providers: {}
                """);
            using var app = await HarnessComposer.Compose(new HarnessOptions(new HarnessHome(home), Directory.GetCurrentDirectory()));
            var commands = app.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
            var agent = new FakeAgent(app.Ctx);

            var result = await commands.Execute(agent, "/provider add custom --base-url http://127.0.0.1:11434/v1 --api-key sk-test --model-ids custom-model", TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.IsType<CommandResult.Success>(result.Result);
            var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
            Assert.Contains(llm.ListProviders(), provider => provider.Id == "custom");
            var settings = HarnessSettings.Load(new HarnessHome(home));
            Assert.Equal("custom/custom-model", settings.GlobalDefaultModel);
            Assert.True(settings.Providers["custom"].Models.ContainsKey("custom-model"));
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    /** `/provider catalog [query] [--page N] [--all]`: 按 id/显示名过滤, 默认分页, --all 全量。 */
    [Fact]
    public async Task ProviderCatalog_Filters_And_Pages()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-provider-catalog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            File.WriteAllText(Path.Combine(home, "settings.yaml"), """
                global_default_model: null
                providers: {}
                """);
            var harnessHome = new HarnessHome(home);
            Directory.CreateDirectory(harnessHome.CachePath);
            var entries = Enumerable.Range(1, 30).Select(index =>
                $"\"provider-{index:00}\": {{\"id\": \"provider-{index:00}\", \"name\": \"Provider {index:00}\", "
                + "\"npm\": \"@ai-sdk/openai-compatible\", \"models\": {}}");
            File.WriteAllText(ProviderCatalog.CacheFile(harnessHome), $"{{{string.Join(',', entries)}}}");
            using var app = await HarnessComposer.Compose(new HarnessOptions(harnessHome, Directory.GetCurrentDirectory()));
            var commands = app.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
            var agent = new FakeAgent(app.Ctx);

            var firstPage = await commands.Execute(agent, "/provider catalog", TestContext.Current.CancellationToken);
            var firstText = Assert.IsType<CommandResult.Success>(firstPage!.Result).Text ?? "";
            Assert.Contains("30 providers", firstText);
            Assert.Contains("provider-01", firstText);
            Assert.DoesNotContain("provider-30", firstText);
            Assert.Contains("第 1/2 页 · 共 30 项 · 下一页: /provider catalog --page 2", firstText);

            var secondPage = await commands.Execute(agent, "/provider catalog --page 2", TestContext.Current.CancellationToken);
            Assert.Contains("provider-30", Assert.IsType<CommandResult.Success>(secondPage!.Result).Text ?? "");

            var filtered = await commands.Execute(agent, "/provider catalog provider-07", TestContext.Current.CancellationToken);
            var filteredText = Assert.IsType<CommandResult.Success>(filtered!.Result).Text ?? "";
            Assert.Contains("provider-07", filteredText);
            Assert.DoesNotContain("provider-08", filteredText);

            var noMatch = await commands.Execute(agent, "/provider catalog zzzz", TestContext.Current.CancellationToken);
            Assert.Contains("无匹配", Assert.IsType<CommandResult.Success>(noMatch!.Result).Text ?? "");
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    /** `--model-ids <all>`: 展开为 models.dev 目录里该 provider 的全部模型; 目录里没有该 provider 时报错。 */
    [Fact]
    public async Task ProviderAdd_AllModelsMarker_ExpandsFromCatalog()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-provider-all", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            File.WriteAllText(Path.Combine(home, "settings.yaml"), """
                global_default_model: null
                providers: {}
                """);
            var harnessHome = new HarnessHome(home);
            Directory.CreateDirectory(harnessHome.CachePath);
            File.WriteAllText(ProviderCatalog.CacheFile(harnessHome), ProviderCatalogTests.Fixture);
            using var app = await HarnessComposer.Compose(new HarnessOptions(harnessHome, Directory.GetCurrentDirectory()));
            var commands = app.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
            var agent = new FakeAgent(app.Ctx);

            var result = await commands.Execute(
                agent,
                "/provider add zhipuai-coding-plan --base-url https://open.bigmodel.cn/api/coding/paas/v4 --api-key sk-test --model-ids <all>",
                TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.IsType<CommandResult.Success>(result.Result);
            var settings = HarnessSettings.Load(harnessHome);
            Assert.True(settings.Providers["zhipuai-coding-plan"].Models.ContainsKey("glm-5.3"));

            var missing = await commands.Execute(
                agent,
                "/provider add not-in-catalog --base-url http://127.0.0.1:1/v1 --api-key sk-test --model-ids <all>",
                TestContext.Current.CancellationToken);

            Assert.NotNull(missing);
            Assert.IsType<CommandResult.Error>(missing.Result);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    /** type 词汇: canonical / custom 别名 / 旧值 都归一化成落盘名, 且不再写 options.apiStyle。 */
    [Theory]
    [InlineData("openai-compatible", ProviderTypes.OpenAiCompatible)]
    [InlineData("openai-compatible(response)", ProviderTypes.OpenAiCompatibleResponses)]
    [InlineData("custom(openai-compatible)", ProviderTypes.OpenAiCompatible)]
    [InlineData("custom(openai-compatible-response)", ProviderTypes.OpenAiCompatibleResponses)]
    [InlineData("anthropic-messages", ProviderTypes.AnthropicMessages)]
    [InlineData("custom(anthropic-messages)", ProviderTypes.AnthropicMessages)]
    [InlineData("anthropic", ProviderTypes.AnthropicMessages)]
    [InlineData("custom(anthropic)", ProviderTypes.AnthropicMessages)]
    [InlineData("openai-responses", ProviderTypes.OpenAiCompatibleResponses)]
    [InlineData("deepseek", ProviderTypes.DeepSeek)]
    public async Task ProviderAdd_Normalizes_Type_To_Canonical(string type, string expected)
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-provider-type", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            File.WriteAllText(Path.Combine(home, "settings.yaml"), """
                global_default_model: null
                providers: {}
                """);
            using var app = await HarnessComposer.Compose(new HarnessOptions(new HarnessHome(home), Directory.GetCurrentDirectory()));
            var commands = app.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
            var agent = new FakeAgent(app.Ctx);

            var result = await commands.Execute(
                agent,
                $"/provider add acme --base-url http://127.0.0.1:11434/v1 --api-key sk-test --type {type} --model-ids m1",
                TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.IsType<CommandResult.Success>(result.Result);
            var settings = HarnessSettings.Load(new HarnessHome(home));
            Assert.Equal(expected, settings.Providers["acme"].Type);
            Assert.Null(settings.Providers["acme"].Options?.ApiStyle);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(Context ctx)
        {
            Ctx = ctx;
            var id = SessionId.Create($"session-{Guid.NewGuid():N}");
            Session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 0,
                Cwd = Path.GetTempPath(),
                IsSeeded = false,
            });
        }

        public SessionId Id => Session.Id;
        public Session Session { get; }
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; }
        public AgentStatus Status => AgentStatus.Idle;
        public AgentOptions Options { get; } = new();

        public void Cancel(AgentCancelCause cause, bool keepInbox = false)
        {
        }

        public Task WhenIdle() => Task.CompletedTask;

        public void Send(UserMessage message, string target, bool wakeup)
        {
        }

        public void Followup(UserMessage message)
        {
        }

        public void Steer(UserMessage message)
        {
        }

        public void Inject(UserMessage message)
        {
        }
    }
}
