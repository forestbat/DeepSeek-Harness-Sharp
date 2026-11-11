using System.Net;
using System.Text;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Web;
using Dsh.Agent;

namespace Dsh.Tests;

/**
 * GitHub and Stack Overflow search engines.
 * Every response body under Web/ApiSamples was captured with curl from the real public APIs on 2026-10-15 and is replayed by a local handler.
 * The only edit is the client IP in github-search-rate-limited.json, replaced by a documentation address.
 */
public sealed class SiteSearchEngineTests : IDisposable
{
    /** Test-only credential values: they never reach a network and exist to prove the requests carry them and errors hide them. */
    private const string TestGitHubToken = "ghp_testOnlyToken0123456789abcdefABCDEF";
    private const string TestStackExchangeKey = "testOnlyKey((";

    private readonly Context _ctx = new();
    private readonly ToolRuntime _tools;
    private readonly WebRuntime _web;
    private readonly SystemPrompt _prompt;
    private readonly IDisposable _toolWeb;

    public SiteSearchEngineTests()
    {
        _prompt = new SystemPrompt(_ctx, new SystemPromptConfig());
        _tools = new ToolRuntime(_ctx);
        _web = new WebRuntime(_ctx);
        _toolWeb = ToolWeb.Apply(_ctx, new ToolWebConfig());
    }

    public void Dispose() => _toolWeb.Dispose();

    private sealed record RecordedRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers);

    /** Serves one captured body for every request and records what the engine sent. */
    private sealed class ReplayHandler(string sample, HttpStatusCode status = HttpStatusCode.OK, IReadOnlyDictionary<string, string>? headers = null) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.ToDictionary(header => header.Key.ToLowerInvariant(), header => string.Join(",", header.Value))));
            var body = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Web", "ApiSamples", sample), cancellationToken);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
                response.Headers.TryAddWithoutValidation(name, value);
            return response;
        }
    }

    private sealed class GeneralSearchStub(bool available) : IWebSearchProvider
    {
        public string Id => "general-stub";
        public bool Available() => available;
        public Task<WebSearchResult> Search(WebSearchRequest request, CancellationToken signal)
            => Task.FromResult(new WebSearchResult(null, [new WebSearchSource("https://general.test")], false));
    }

    private Task<ToolExecutionResult> SearchTool(string arguments)
        => _tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = WebSearchTool.ToolName,
            Arguments = JsonDocument.Parse(arguments).RootElement,
            Signal = TestContext.Current.CancellationToken,
        });

    private static Dictionary<string, string> QueryOf(Uri uri)
        => uri.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    [Fact]
    public async Task GitHubRepositories_MapsCapturedResponseWithoutToken()
    {
        var handler = new ReplayHandler("github-search-repositories.json");
        _web.RegisterSearchProvider(new GitHubRepositorySearchProvider(null, new HttpClient(handler)));

        var result = await _web.Search(new WebSearchRequest("roslyn mcp server", 3, GitHubRepositorySearchProvider.ProviderId), TestContext.Current.CancellationToken);

        Assert.Equal(GitHubRepositorySearchProvider.ProviderId, result.Engine);
        Assert.Equal(3, result.Sources.Count);
        Assert.True(result.Truncated);
        var first = result.Sources[0];
        Assert.Equal("https://github.com/JoshuaRamirez/RoslynMcpServer", first.Url);
        Assert.Equal("JoshuaRamirez/RoslynMcpServer", first.Title);
        Assert.StartsWith("Model Context Protocol server for Roslyn-powered C# refactoring operations · 31 stars · C# · topics: ai-tools,", first.Snippet);
        Assert.Contains("last push 2026-10-15", first.Snippet);
        Assert.Contains("Unauthenticated: 10 search requests per minute", result.Limitations);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/search/repositories", request.Uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("roslyn mcp server", QueryOf(request.Uri)["q"]);
        Assert.Equal("3", QueryOf(request.Uri)["per_page"]);
        Assert.False(request.Headers.ContainsKey("authorization"));
        Assert.Equal(GitHubSearchProvider.ApiVersion, request.Headers["x-github-api-version"]);
        Assert.Contains("deepseek-harness", request.Headers["user-agent"]);
    }

    [Fact]
    public async Task GitHubCode_SendsTokenAndMapsTextMatches()
    {
        var handler = new ReplayHandler("github-search-code.json");
        _web.RegisterSearchProvider(new GitHubCodeSearchProvider(TestGitHubToken, new HttpClient(handler)));

        var result = await _web.Search(new WebSearchRequest("roslyn mcp server", 3, GitHubCodeSearchProvider.ProviderId), TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Sources.Count);
        Assert.Equal("sillsdev/FieldWorks: Docs/mcp.md", result.Sources[0].Title);
        Assert.Equal("https://github.com/sillsdev/FieldWorks/blob/b1d2482822a6829e7c8a70385ecb78299b9d2fba/Docs/mcp.md", result.Sources[0].Url);
        Assert.StartsWith("- **Headless screenshot is blank or stale** – pass the FieldWorks process ID", result.Sources[0].Snippet);
        Assert.EndsWith("…", result.Sources[0].Snippet);
        Assert.Contains("default branches only", result.Limitations);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"Bearer {TestGitHubToken}", request.Headers["authorization"]);
        Assert.Equal("application/vnd.github.text-match+json", request.Headers["accept"]);
    }

    [Fact]
    public async Task GitHubCode_WithoutTokenIsUnavailableAndNamesTheSetting()
    {
        using var registration = WebSearchGitHub.Apply(_ctx, new Dictionary<string, object?>());
        Assert.Equal([GitHubRepositorySearchProvider.ProviderId], _web.SearchEngines().Select(engine => engine.Id));

        var error = await Assert.ThrowsAsync<WebError>(() => _web.Search(new WebSearchRequest("q", null, GitHubCodeSearchProvider.ProviderId), TestContext.Current.CancellationToken));

        Assert.Equal(WebErrorCodes.InvalidArgs, error.Code);
        Assert.Contains(GitHubSearchProvider.TokenSetting, error.Message);
        Assert.Contains("usable engines: github-repos", error.Message);
    }

    [Fact]
    public void GitHubPlugin_RegistersCodeEngineWhenTokenIsConfigured()
    {
        using var registration = WebSearchGitHub.Apply(_ctx, new Dictionary<string, object?> { [WebSearchGitHub.TokenKey] = TestGitHubToken });
        Assert.Equal([GitHubCodeSearchProvider.ProviderId, GitHubRepositorySearchProvider.ProviderId], _web.SearchEngines().Select(engine => engine.Id));
    }

    [Fact]
    public async Task GitHub_RateLimitErrorCarriesResetTimeAndTokenHint()
    {
        var handler = new ReplayHandler("github-search-rate-limited.json", HttpStatusCode.Forbidden, new Dictionary<string, string>
        {
            ["x-ratelimit-limit"] = "10",
            ["x-ratelimit-remaining"] = "0",
            ["x-ratelimit-reset"] = "1792092864",
        });
        var provider = new GitHubRepositorySearchProvider(null, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<WebError>(() => provider.Search(new WebSearchRequest("x"), TestContext.Current.CancellationToken));

        Assert.Equal(WebErrorCodes.ProviderError, error.Code);
        Assert.Contains("HTTP 403", error.Message);
        Assert.Contains("API rate limit exceeded", error.Message);
        Assert.Contains("exhausted until 2026-10-15 19:34:24 UTC", error.Message);
        Assert.Contains(GitHubSearchProvider.TokenSetting, error.Message);
    }

    [Fact]
    public async Task GitHub_CodeSearchRejectionSurfacesApiMessage()
    {
        var handler = new ReplayHandler("github-search-code-unauthenticated.json", HttpStatusCode.Unauthorized);
        var provider = new GitHubCodeSearchProvider(TestGitHubToken, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<WebError>(() => provider.Search(new WebSearchRequest("x"), TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 401", error.Message);
        Assert.Contains("Requires authentication", error.Message);
        Assert.DoesNotContain(TestGitHubToken, error.Message);
    }

    [Fact]
    public async Task StackOverflow_MapsCapturedResponse()
    {
        var handler = new ReplayHandler("stackexchange-search-advanced.json");
        _web.RegisterSearchProvider(new StackOverflowSearchProvider(null, new HttpClient(handler)));

        var result = await _web.Search(new WebSearchRequest("roslyn analyzer code fix", 3, StackOverflowSearchProvider.ProviderId), TestContext.Current.CancellationToken);

        Assert.Equal(StackOverflowSearchProvider.ProviderId, result.Engine);
        Assert.Equal(3, result.Sources.Count);
        Assert.True(result.Truncated);
        var answered = result.Sources[2];
        Assert.Equal("Why can't I create a new analyzer/code fix provider project after installing the Roslyn SDK?", answered.Title);
        Assert.Equal("https://stackoverflow.com/questions/42428697/why-cant-i-create-a-new-analyzer-code-fix-provider-project-after-installing-the", answered.Url);
        Assert.StartsWith("5 votes · 1 answers · accepted answer · tags: ", answered.Snippet);
        Assert.Contains("I'm using Visual Studio 2017 RC.", answered.Snippet);
        Assert.DoesNotContain("<p>", answered.Snippet);
        Assert.Equal("2017-02-24", answered.PublishedAt);
        Assert.StartsWith("8 votes · 2 answers · tags: roslyn, analyzer", result.Sources[0].Snippet);
        Assert.Contains("300 requests per day", result.Limitations);
        var query = QueryOf(Assert.Single(handler.Requests).Uri);
        Assert.Equal("stackoverflow", query["site"]);
        Assert.Equal("withbody", query["filter"]);
        Assert.Equal("3", query["pagesize"]);
        Assert.False(query.ContainsKey("key"));
    }

    [Fact]
    public async Task StackOverflow_ErrorHidesKey()
    {
        var handler = new ReplayHandler("stackexchange-bad-parameter.json", HttpStatusCode.BadRequest);
        var provider = new StackOverflowSearchProvider(TestStackExchangeKey, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<WebError>(() => provider.Search(new WebSearchRequest("x"), TestContext.Current.CancellationToken));

        Assert.Equal(TestStackExchangeKey, QueryOf(Assert.Single(handler.Requests).Uri)["key"]);
        Assert.Contains("bad_parameter: sort", error.Message);
        Assert.Contains("https://api.stackexchange.com/2.3/search/advanced", error.Message);
        Assert.DoesNotContain(TestStackExchangeKey, error.Message);
        Assert.DoesNotContain(Uri.EscapeDataString(TestStackExchangeKey), error.Message);
    }

    [Fact]
    public async Task SiteEngines_DoNotMakeTheDefaultChoiceAmbiguous()
    {
        _web.RegisterSearchProvider(new GitHubRepositorySearchProvider(null, new HttpClient(new ReplayHandler("github-search-repositories.json"))));
        _web.RegisterSearchProvider(new StackOverflowSearchProvider(null, new HttpClient(new ReplayHandler("stackexchange-search-advanced.json"))));
        _web.RegisterSearchProvider(new GeneralSearchStub(available: true));

        var result = await _web.Search(new WebSearchRequest("q"), TestContext.Current.CancellationToken);

        Assert.Equal("general-stub", result.Engine);
    }

    [Fact]
    public async Task SiteEngines_AreSuggestedWhenNoGeneralEngineIsUsable()
    {
        _web.RegisterSearchProvider(new GitHubRepositorySearchProvider(null, new HttpClient(new ReplayHandler("github-search-repositories.json"))));
        _web.RegisterSearchProvider(new StackOverflowSearchProvider(null, new HttpClient(new ReplayHandler("stackexchange-search-advanced.json"))));
        _web.RegisterSearchProvider(new GeneralSearchStub(available: false));

        var error = await Assert.ThrowsAsync<WebError>(() => _web.Search(new WebSearchRequest("q"), TestContext.Current.CancellationToken));

        Assert.Equal(WebErrorCodes.ProviderUnavailable, error.Code);
        Assert.Contains("github-repos", error.Message);
        Assert.Contains("stackoverflow", error.Message);
    }

    [Fact]
    public async Task WebSearchTool_LabelsEngineAndListsEnginesInPrompt()
    {
        _web.RegisterSearchProvider(new StackOverflowSearchProvider(null, new HttpClient(new ReplayHandler("stackexchange-search-advanced.json"))));
        _web.RegisterSearchProvider(new GeneralSearchStub(available: true));

        var result = await SearchTool("""{"queries":["roslyn analyzer","code fix"],"engine":"stackoverflow"}""");

        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Equal("stackoverflow", success.Value.GetProperty("engine").GetString());
        var text = string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.Contains("Engine: stackoverflow\nLimitations: Searches Stack Overflow questions only", text);
        Assert.Equal(1, text.Split("Searches Stack Overflow questions only").Length - 1);
        Assert.Equal("stackoverflow", success.Meta?.GetProperty("engine").GetString());
        var assembly = await _prompt.Assemble(new AssembleContext());
        var section = Assert.Single(assembly.Sections, candidate => candidate.Name == "tool:web_search").Text;
        Assert.Contains("- general-stub: General web search.", section);
        Assert.Contains("- stackoverflow (named only): Stack Overflow questions", section);
    }

    [Fact]
    public async Task WebSearchTool_RejectsUnknownEngine()
    {
        _web.RegisterSearchProvider(new GeneralSearchStub(available: true));

        var result = await SearchTool("""{"queries":["q"],"engine":"nope"}""");

        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Equal(WebErrorCodes.InvalidArgs, failure.Error.Info?.Code);
        Assert.Contains("usable engines: general-stub", failure.Error.Message);
    }
}
