using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cordis;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Web;

namespace Dsh.Tests;

public class WebTests : IDisposable
{
    private readonly Context _ctx = new();
    private readonly ToolRuntime _tools;
    private readonly WebRuntime _web;
    private readonly IDisposable _toolWeb;
    private int _counter;

    public WebTests()
    {
        _ = new SystemPrompt(_ctx, new SystemPromptConfig());
        _tools = new ToolRuntime(_ctx);
        _web = new WebRuntime(_ctx);
        _toolWeb = ToolWeb.Apply(_ctx, new ToolWebConfig());
    }

    public void Dispose()
    {
        _toolWeb.Dispose();
    }

    private Task<ToolExecutionResult> Execute(string name, string arguments)
        => _tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{++_counter}"),
            Name = name,
            Arguments = JsonDocument.Parse(arguments).RootElement,
            Signal = default,
        });

    private static string TextOf(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    private sealed class StubSearchProvider(string id, Func<WebSearchRequest, CancellationToken, Task<WebSearchResult>> search, bool available = true) : IWebSearchProvider
    {
        public string Id { get; } = id;
        public bool Available() => available;
        public Task<WebSearchResult> Search(WebSearchRequest request, CancellationToken signal) => search(request, signal);
    }

    private sealed class StubFetchProvider(string id, Func<WebFetchRequest, CancellationToken, Task<WebFetchResult>> fetch, bool available = true) : IWebFetchProvider
    {
        public string Id { get; } = id;
        public bool Available() => available;
        public Task<WebFetchResult> Fetch(WebFetchRequest request, CancellationToken signal) => fetch(request, signal);
    }

    public sealed class SearchFormatting
    {
        [Fact]
        public void FormatsSourcesContentAndNoResults()
        {
            var output = WebSearchTool.FormatSearchOutput(new WebSearchResultValue(
                "an answer",
                [
                    new WebSearchSource("https://a.test/x", "A", "about a", "2026-01-01"),
                    new WebSearchSource("https://b.test/y"),
                ],
                false));
            Assert.Contains("[A](https://a.test/x) — about a (2026-01-01)", output);
            Assert.Contains("[b.test](https://b.test/y)", output);
            Assert.Contains("Cite the relevant URLs", output);

            var empty = WebSearchTool.FormatSearchOutput(new WebSearchResultValue(null, [], false));
            Assert.Contains("No results found.", empty);
        }

        [Fact]
        public void ValidatesQueries()
        {
            Assert.Equal(["hi"], WebSearchTool.ParseSearchArgs(JsonDocument.Parse("""{"queries":["hi"]}""").RootElement, 4));
            Assert.Equal(["one", " two "], WebSearchTool.ParseSearchArgs(JsonDocument.Parse("""{"queries":["one","one"," two "]}""").RootElement, 4));
            Assert.Throws<WebError>(() => WebSearchTool.ParseSearchArgs(JsonDocument.Parse("""{"queries":[]}""").RootElement, 4));
            Assert.Throws<WebError>(() => WebSearchTool.ParseSearchArgs(JsonDocument.Parse("""{"queries":["one","two"]}""").RootElement, 1));
            Assert.Throws<WebError>(() => WebSearchTool.ParseSearchArgs(JsonDocument.Parse("""{"queries":["ok"," "]}""").RootElement, 4));
        }
    }

    public sealed class FetchFormatting
    {
        [Fact]
        public void ConvertsHtmlAndTruncates()
        {
            var result = new WebFetchResult(
                "https://a.test",
                200,
                new WebFetchBody("html", "<h1>Title</h1><p>Body text</p>"),
                false);
            var output = WebFetchTool.FormatFetchOutput(result, 1_000_000);
            Assert.Contains("Fetched https://a.test (HTTP 200)", output);
            Assert.Contains("# Title", output);
            Assert.Contains("Body text", output);

            var plain = new WebFetchResult("https://a.test", 200, new WebFetchBody("text", "plain"), true);
            var truncated = WebFetchTool.FormatFetchOutput(plain, 1_000_000);
            Assert.Contains("plain", truncated);
            Assert.Contains("Content truncated", truncated);
        }

        [Fact]
        public void BoundsCompleteOutput()
        {
            var result = new WebFetchResult(
                "https://a.test",
                200,
                new WebFetchBody("html", $"<p>{new string('_', 1000)}</p>"),
                false);
            var output = WebFetchTool.FormatFetchOutput(result, 500);
            Assert.True(output.Length <= 500);
            Assert.Contains("Content truncated", output);
        }

        [Fact]
        public void OmitsDeeplyNestedHtml()
        {
            var depth = 20_000;
            var html = new string('<', 0) + string.Concat(Enumerable.Repeat("<div>", depth)) + "x" + string.Concat(Enumerable.Repeat("</div>", depth));
            var result = new WebFetchResult("https://a.test", 200, new WebFetchBody("html", html), false);
            Assert.Contains("[HTML content omitted: unable to convert safely.]", WebFetchTool.FormatFetchOutput(result, 1_000_000));
        }
    }

    public sealed class ToolExecution
    {
        [Fact]
        public async Task WebSearchExecutesAndMerges()
        {
            using var host = new WebTests();
            var called = new List<string>();
            host._web.RegisterSearchProvider(new StubSearchProvider("stub-search", async (request, _) =>
            {
                called.Add(request.Query);
                return request.Query == "one"
                    ? new WebSearchResult("answer one", [new WebSearchSource("https://a.test", "A"), new WebSearchSource("https://shared.test")], false)
                    : new WebSearchResult("answer two", [new WebSearchSource("https://b.test", "B"), new WebSearchSource("https://shared.test")], false);
            }));

            var result = await host.Execute("web_search", """{"queries":["one","one","two"]}""");
            Assert.False(result.IsError);
            var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
            Assert.Equal("https://a.test", value.GetProperty("sources")[0].GetProperty("url").GetString());
            Assert.Equal("https://b.test", value.GetProperty("sources")[1].GetProperty("url").GetString());
            Assert.Equal("https://shared.test", value.GetProperty("sources")[2].GetProperty("url").GetString());
            Assert.Contains("### one", TextOf(result));
            Assert.Contains("### two", TextOf(result));
        }

        [Fact]
        public async Task WebSearchSurfacesProviderUnavailable()
        {
            using var host = new WebTests();
            var result = await host.Execute("web_search", """{"queries":["q"]}""");
            Assert.True(result.IsError);
            Assert.Equal("WEB_PROVIDER_UNAVAILABLE", Assert.IsType<ToolExecutionResult.Failure>(result).Error.Info?.Code);
        }

        [Fact]
        public async Task WebFetchExecutesAndProjectsMeta()
        {
            using var host = new WebTests();
            host._web.RegisterFetchProvider(new StubFetchProvider("stub-fetch", (request, _) =>
                Task.FromResult(new WebFetchResult(request.Url, 200, new WebFetchBody("text", "ok"), true))));

            var result = await host.Execute("web_fetch", """{"url":"https://a.test"}""");
            Assert.False(result.IsError, TextOf(result));
            var success = Assert.IsType<ToolExecutionResult.Success>(result);
            Assert.Equal("https://a.test", success.Value.GetProperty("url").GetString());
            Assert.Equal(200, success.Value.GetProperty("statusCode").GetInt32());
            Assert.Equal("ok", success.Value.GetProperty("body").GetProperty("content").GetString());
            Assert.True(success.Meta?.GetProperty("truncated").GetBoolean());
        }

        [Fact]
        public async Task WebFetchOutputCapBoundsRenderedText()
        {
            using var host = new WebTests();
            host._web.RegisterFetchProvider(new StubFetchProvider("stub-fetch", (_, _) =>
                Task.FromResult(new WebFetchResult("https://a.test", 200, new WebFetchBody("html", $"<p>{new string('_', 1000)}</p>"), false))));
            host._toolWeb.Dispose();
            _ = ToolWeb.Apply(host._ctx, new ToolWebConfig { FetchMaxOutputChars = 100 });

            var result = await host.Execute("web_fetch", """{"url":"https://a.test"}""");
            Assert.False(result.IsError, TextOf(result));
            Assert.True(TextOf(result).Length <= 100);
        }
    }

    public sealed class WebRuntimeTests
    {
        [Fact]
        public void DuplicateProviderThrows()
        {
            using var host = new WebTests();
            var provider = new StubSearchProvider("stub-search", (_, _) => Task.FromResult(new WebSearchResult(null, [], false)));
            host._web.RegisterSearchProvider(provider);
            Assert.Throws<WebError>(() => host._web.RegisterSearchProvider(provider));
        }

        [Fact]
        public async Task AmbiguousProvidersThrow()
        {
            using var host = new WebTests();
            host._web.RegisterSearchProvider(new StubSearchProvider("one", (_, _) => Task.FromResult(new WebSearchResult(null, [], false)), available: true));
            host._web.RegisterSearchProvider(new StubSearchProvider("two", (_, _) => Task.FromResult(new WebSearchResult(null, [], false)), available: true));
            var error = await Assert.ThrowsAsync<WebError>(() => host._web.Search(new WebSearchRequest("q")));
            Assert.Equal(WebErrorCodes.ProviderAmbiguous, error.Code);
        }
    }

    public sealed class DeepSeekMapping
    {
        [Fact]
        public void MapsCitationSnippetsAndDedupes()
        {
            var response = JsonDocument.Parse("""
                {
                  "content": [
                    { "type": "text", "text": "found", "citations": [{ "url": "https://a.test", "cited_text": "excerpt for A" }] },
                    {
                      "type": "web_search_tool_result",
                      "content": [
                        { "type": "web_search_result", "url": "https://a.test", "title": "A", "page_age": "2026-02-02" },
                        { "type": "web_search_result", "url": "https://a.test", "title": "second" },
                        { "type": "web_search_result", "url": "https://b.test", "title": "B" }
                      ]
                    }
                  ]
                }
                """).RootElement;

            var result = DeepSeekSearchProvider.MapAnthropicResponse(response);
            Assert.Equal(2, result.Sources.Count);
            Assert.Equal("https://a.test", result.Sources[0].Url);
            Assert.Equal("excerpt for A", result.Sources[0].Snippet);
            Assert.Equal("2026-02-02", result.Sources[0].PublishedAt);
            Assert.Equal("https://b.test", result.Sources[1].Url);
        }

        [Fact]
        public void RejectsProseOnlyResponse()
        {
            var response = JsonDocument.Parse("""{"content":[{"type":"text","text":"no search"}]}""").RootElement;
            var error = Assert.Throws<WebError>(() => DeepSeekSearchProvider.MapAnthropicResponse(response));
            Assert.Equal(WebErrorCodes.ProviderError, error.Code);
        }
    }

    public sealed class FetchHttpPolicyTests
    {
        [Fact]
        public void ValidatesSchemesAndClassifies()
        {
            Assert.Equal("https", WebFetchHttpPolicy.ValidateFetchUrl("https://example.com/x").Scheme);
            Assert.Throws<WebError>(() => WebFetchHttpPolicy.ValidateFetchUrl("ftp://example.com"));
            Assert.Throws<WebError>(() => WebFetchHttpPolicy.ValidateFetchUrl("https://user:pass@example.com"));
            Assert.Equal("html", WebFetchHttpPolicy.ClassifyContentType("text/html; charset=utf-8"));
            Assert.Equal("text", WebFetchHttpPolicy.ClassifyContentType("application/json"));
            Assert.Null(WebFetchHttpPolicy.ClassifyContentType("image/png"));
            Assert.Equal("utf-8", WebFetchHttpPolicy.ParseCharset("text/html; charset=UTF-8"));
        }

        [Fact]
        public void PublicIpRejectsLoopbackAndPrivate()
        {
            Assert.True(WebFetchHttpNetwork.IsPublicIpAddress("8.8.8.8"));
            Assert.False(WebFetchHttpNetwork.IsPublicIpAddress("127.0.0.1"));
            Assert.False(WebFetchHttpNetwork.IsPublicIpAddress("10.0.0.1"));
            Assert.False(WebFetchHttpNetwork.IsPublicIpAddress("::1"));
        }

        [Fact]
        public async Task FetchesLocalTextBodyThroughPinnedConnection()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var buffer = new byte[4096];
                var received = new List<byte>();
                while (true)
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0)
                        break;
                    received.AddRange(buffer.AsSpan(0, read).ToArray());
                    if (Encoding.ASCII.GetString(received.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
                        break;
                }
                var body = Encoding.ASCII.GetBytes("ok");
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(body);
            });

            try
            {
                var provider = new HttpFetchProvider(
                    new HttpFetchLimits { MaxResponseBytes = 1024, MaxBodyChars = 1024, TimeoutMs = 5000, MaxRedirects = 0, UserAgent = "test" },
                    (_, _) => Task.FromResult<IReadOnlyList<PublicAddress>>([new PublicAddress("127.0.0.1", 4)]));
                var result = await provider.Fetch(new WebFetchRequest($"http://127.0.0.1:{port}/"));
                Assert.Equal(200, result.StatusCode);
                Assert.Equal("ok", result.Body.Content);
            }
            finally
            {
                listener.Stop();
                await serverTask;
            }
        }
    }
}