using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime.Events;
using Dsh.Web;
using System.Text.Json;

namespace Dsh.Tests;

/**
 * Live calls through the real search plugins/HTTP clients, plus an actual LLM tool-use turn.
 * They share the LlmSmoke category with the other networked tests so CI and quick runs exclude them.
 */
[Trait("Category", "LlmSmoke")]
public sealed class SiteSearchSmokeTests
{
    [Fact]
    public async Task Bing_LiveSearchReturnsSourceLinks()
    {
        var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var registration = WebSearchGeneral.Apply(ctx, "bing");
        var result = await web.Search(new WebSearchRequest("roslyn", 3, "bing"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(result.Sources);
        Assert.Equal("bing", result.Engine);
        Console.WriteLine($"Bing live RSS returned {result.Sources.Count} sources; first: {result.Sources[0].Url}");
    }

    [Fact]
    public async Task DuckDuckGo_LiveEntryReturnsSourcesOrExplicitSiteRestriction()
    {
        var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var registration = WebSearchGeneral.Apply(ctx, "duckduckgo");
        try
        {
            var result = await web.Search(new WebSearchRequest("roslyn", 3, "duckduckgo"), TestContext.Current.CancellationToken);
            Assert.NotEmpty(result.Sources);
            Console.WriteLine($"DuckDuckGo live HTML returned {result.Sources.Count} sources; first: {result.Sources[0].Url}");
        }
        catch (WebError error) when (error.Message.Contains("HTTP 202", StringComparison.Ordinal) || error.Message.Contains("human challenge", StringComparison.Ordinal))
        {
            Assert.Equal(WebErrorCodes.ProviderError, error.Code);
            Console.WriteLine($"DuckDuckGo live restriction (not a successful search): {error.Message}");
        }
    }

    [Theory]
    [InlineData("google")]
    [InlineData("baidu")]
    public async Task CredentialedEngine_LiveEndpointRejectsInvalidKey(string engine)
    {
        var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var registration = WebSearchGeneral.Apply(ctx, engine, new Dictionary<string, object?>
        {
            ["apiKey"] = "invalid-key-for-live-authentication-check",
            ["searchEngineId"] = "invalid-cx-for-live-authentication-check",
        });
        var error = await Assert.ThrowsAsync<WebError>(() => web.Search(new WebSearchRequest("roslyn", 3, engine), TestContext.Current.CancellationToken));
        Assert.Equal(WebErrorCodes.ProviderError, error.Code);
        Assert.DoesNotContain("invalid-key-for-live-authentication-check", error.Message);
        Assert.True(error.Message.Contains("HTTP 400", StringComparison.Ordinal) || error.Message.Contains("HTTP 401", StringComparison.Ordinal) || error.Message.Contains("HTTP 403", StringComparison.Ordinal), error.Message);
        Console.WriteLine($"{engine} live credential rejection: {error.Message}");
    }

    [Fact]
    public async Task RealLlm_CallsBingSearchThroughToolAndCitesItsSources()
    {
        var originalHome = HarnessHome.Resolve();
        var settings = HarnessSettings.Load(originalHome);
        var (providerId, modelId) = settings.ResolveDefaultModel()
            ?? throw new InvalidOperationException("Real LLM smoke requires global_default_model in settings.yaml.");
        Assert.True(settings.Providers.TryGetValue(providerId, out var provider), "Real LLM smoke requires the configured default provider in settings.yaml.");
        var apiKey = provider.Options?.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) && provider.Options?.ApiKeyEnv is { Length: > 0 } reference)
            apiKey = new EnvCredentials(originalHome, Directory.GetCurrentDirectory()).Get(reference);
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "Real LLM smoke requires a resolvable credential for the configured provider.");
        var home = Path.Combine(Directory.GetCurrentDirectory(), ".daemon-tests", $"search-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            File.Copy(Path.Combine(originalHome.Root, "settings.yaml"), Path.Combine(home, "settings.yaml"));
            // Exercise the native web_search schema, not the optional TOON wrapper.
            settings.Plugins["@deepseek-ai/dsh-toon"] = new PluginSetting { Enabled = false };
            settings.SavePlugins(HarnessHome.Resolve(home));
            using var app = await HarnessComposer.Compose(new HarnessOptions(HarnessHome.Resolve(home), Cwd: Directory.GetCurrentDirectory(), Provider: providerId, Model: modelId, ApiKey: apiKey));
            var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var handle = await agents.Create(new CreateAgentOptions(SessionId.Create($"search-smoke-{Guid.NewGuid():N}"), Directory.GetCurrentDirectory(), new AgentOptions(providerId, modelId)), TestContext.Current.CancellationToken);
            var agent = (AgentLoopAgent)handle.Agent;
            agent.Followup(MessageFactory.CreateUserText("Call web_search exactly once with engine bing and queries [\"roslyn\"]. Do not call any other tools. In the final response cite one returned source URL and state that Bing RSS ranking may differ from the interactive search."));
            await agent.WhenIdle().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var events = agent.Session.SnapshotEvents().Select(sessionEvent => sessionEvent.Data).ToList();
            var call = Assert.Single(events.OfType<ToolCallPayload>(), call => call.Name == WebSearchTool.ToolName);
            using var arguments = JsonDocument.Parse(call.Arguments);
            Assert.Equal("bing", arguments.RootElement.GetProperty("engine").GetString());
            var result = Assert.Single(events.OfType<ToolResultPayload>());
            Assert.Null(result.Error);
            var text = string.Join("\n", result.Message.Block.Content.OfType<TextBlock>().Select(block => block.Text));
            Assert.Contains("Engine: bing", text);
            Assert.Contains("Sources:", text);
            var final = string.Join("\n", events.OfType<AssistantMessagePayload>().SelectMany(payload => payload.Message.Content).OfType<TextBlock>().Select(block => block.Text));
            var metadata = WebSearchTool.SearchMetaFromResult(result.Meta);
            Assert.NotNull(metadata);
            Assert.NotEmpty(metadata.Sources);
            Assert.Contains(metadata.Sources, source => final.Contains(source.Url, StringComparison.Ordinal));
            Console.WriteLine("Real LLM invoked web_search(engine=bing), received sources and cited a URL.");
        }
        finally
        {
            TempTree.Delete(home);
        }
    }
    [Fact]
    public async Task GitHubRepositories_LiveSearchReturnsRepositories()
    {
        var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var registration = WebSearchGitHub.Apply(ctx);

        var result = await web.Search(new WebSearchRequest("roslyn mcp server", 3, GitHubRepositorySearchProvider.ProviderId), TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Sources);
        Assert.All(result.Sources, source => Assert.StartsWith("https://github.com/", source.Url));
    }

    [Fact]
    public async Task StackOverflow_LiveSearchReturnsQuestions()
    {
        var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var registration = WebSearchStackOverflow.Apply(ctx);

        var result = await web.Search(new WebSearchRequest("roslyn analyzer code fix", 3, StackOverflowSearchProvider.ProviderId), TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Sources);
        Assert.All(result.Sources, source => Assert.StartsWith("https://stackoverflow.com/questions/", source.Url));
    }
}
