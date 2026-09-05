using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cordis;

namespace Dsh.Web;

public sealed class DeepSeekSearchProviderOptions
{
    public string? ApiKey { get; init; }
    public Func<Task<string?>>? ResolveApiKey { get; init; }
    public string BaseUrl { get; init; } = "https://api.deepseek.com/anthropic/v1";
    public string Model { get; init; } = "deepseek-v4-flash";
    public string ApiVersion { get; init; } = "2023-06-01";
    public int MaxTokens { get; init; } = 4096;
    public int MaxUses { get; init; } = 5;
    public Action<DeepSeekSearchLlmRequest>? RecordRequest { get; init; }
}

public sealed record DeepSeekSearchLlmRequest(string Endpoint, string ApiVersion, object Body);

public sealed class DeepSeekSearchConfig
{
    public string? ApiKey { get; init; }
    public string BaseUrl { get; init; } = "https://api.deepseek.com/anthropic/v1";
    public string Model { get; init; } = "deepseek-v4-flash";
    public string ApiVersion { get; init; } = "2023-06-01";
    public int MaxTokens { get; init; } = 4096;
    public int MaxUses { get; init; } = 5;
}

public sealed class DeepSeekSearchProvider : IWebSearchProvider
{
    public const string ProviderId = "deepseek-official";
    public const string DefaultBaseUrl = "https://api.deepseek.com/anthropic/v1";
    public const string DefaultModel = "deepseek-v4-flash";
    public const string DefaultApiVersion = "2023-06-01";
    public const int DefaultMaxTokens = 4096;
    public const int DefaultMaxUses = 5;
    public const string UserAgent = "deepseek-harness/0.0.1";

    private readonly Func<DeepSeekSearchProviderOptions> _resolveOptions;
    private readonly HttpClient _http;

    public DeepSeekSearchProvider(Func<DeepSeekSearchProviderOptions> resolveOptions, HttpClient? http = null)
    {
        _resolveOptions = resolveOptions;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true);
    }

    public string Id => ProviderId;

    public bool Available()
    {
        var options = _resolveOptions();
        return ((options.ApiKey?.Length ?? 0) > 0 || options.ResolveApiKey is not null)
            && Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _)
            && options.MaxTokens > 0
            && options.MaxUses > 0;
    }

    public async Task<WebSearchResult> Search(WebSearchRequest request, CancellationToken signal = default)
    {
        var options = _resolveOptions();
        var apiKey = await ResolveApiKeyAsync(options, signal).ConfigureAwait(false);
        ThrowIfAborted(signal);
        var endpoint = $"{options.BaseUrl.TrimEnd('/')}/messages";
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["max_tokens"] = options.MaxTokens,
            ["messages"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new[]
                    {
                        new Dictionary<string, object?> { ["type"] = "text", ["text"] = $"Perform a web search for the query: {request.Query}" },
                    },
                },
            },
            ["tools"] = new[]
            {
                new Dictionary<string, object?> { ["type"] = "web_search_20250305", ["name"] = "web_search", ["max_uses"] = options.MaxUses },
            },
        };
        options.RecordRequest?.Invoke(new DeepSeekSearchLlmRequest(endpoint, options.ApiVersion, body));
        ThrowIfAborted(signal);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        httpRequest.Headers.TryAddWithoutValidation("authorization", $"Bearer {apiKey}");
        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", options.ApiVersion);
        httpRequest.Headers.TryAddWithoutValidation("user-agent", UserAgent);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw SearchAborted(signal);
        }
        catch (Exception error)
        {
            throw SearchEndpointError(endpoint, $"DeepSeek search request failed: {error.Message}", error);
        }

        using (response)
        {
            if (IsRedirectStatus((int)response.StatusCode))
            {
                throw SearchEndpointError(endpoint, $"DeepSeek search request failed: redirect response (HTTP {(int)response.StatusCode})");
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = $"DeepSeek API error (HTTP {(int)response.StatusCode})";
                try
                {
                    await using var errorStream = await response.Content.ReadAsStreamAsync(signal).ConfigureAwait(false);
                    using var errorDocument = await JsonDocument.ParseAsync(errorStream, cancellationToken: signal).ConfigureAwait(false);
                    var detail = ReadErrorDetail(errorDocument.RootElement);
                    if (detail is { Length: > 0 })
                        message += $": {detail}";
                }
                catch (OperationCanceledException)
                {
                    throw SearchAborted(signal);
                }
                catch
                {
                }
                throw SearchEndpointError(endpoint, message);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(signal).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: signal).ConfigureAwait(false);
                var result = MapAnthropicResponse(document.RootElement);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw SearchAborted(signal);
            }
            catch (WebError error)
            {
                throw SearchEndpointError(endpoint, error.Message, error);
            }
            catch (Exception error)
            {
                throw SearchEndpointError(endpoint, $"DeepSeek returned an unprocessable response body: {error.Message}", error);
            }
        }
    }

    public static IReadOnlyDictionary<string, string> CitationSnippets(JsonElement blocks)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (blocks.ValueKind != JsonValueKind.Array)
            return map;
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type) || type.GetString() != "text")
                continue;
            if (!block.TryGetProperty("citations", out var citations) || citations.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var cite in citations.EnumerateArray())
            {
                if (cite.ValueKind != JsonValueKind.Object)
                    continue;
                var url = cite.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                var citedText = cite.TryGetProperty("cited_text", out var citedElement) ? citedElement.GetString() : null;
                if (url is { Length: > 0 } && citedText is { Length: > 0 } && !map.ContainsKey(url))
                    map[url] = citedText;
            }
        }
        return map;
    }

    public static WebSearchResult MapAnthropicResponse(JsonElement response)
    {
        var blocks = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("content", out var contentElement) ? contentElement : default;
        var blockList = blocks.ValueKind == JsonValueKind.Array ? blocks : default;
        var resultBlocks = new List<JsonElement>();
        if (blockList.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in blockList.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var type) && type.GetString() == "web_search_tool_result")
                    resultBlocks.Add(block);
            }
        }
        if (resultBlocks.Count == 0)
        {
            throw new WebError(
                "DeepSeek returned no web_search_tool_result blocks; the request may not have triggered native web search",
                WebErrorCodes.ProviderError);
        }

        var snippets = CitationSnippets(blockList);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<WebSearchSource>();
        foreach (var block in resultBlocks)
        {
            if (!block.TryGetProperty("content", out var items) || items.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!item.TryGetProperty("type", out var type) || type.GetString() != "web_search_result")
                    continue;
                if (!item.TryGetProperty("url", out var urlElement) || urlElement.GetString() is not { Length: > 0 } url)
                    continue;
                if (!seen.Add(url))
                    continue;
                var title = OptionalString(item, "title");
                var pageAge = OptionalString(item, "page_age");
                snippets.TryGetValue(url, out var snippet);
                sources.Add(new WebSearchSource(
                    url,
                    title is { Length: > 0 } ? title : null,
                    snippet is { Length: > 0 } ? snippet : null,
                    pageAge is { Length: > 0 } ? pageAge : null));
            }
        }
        return new WebSearchResult(null, sources, false);
    }

    private static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string? ReadErrorDetail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
                return message.GetString();
        }
        if (root.TryGetProperty("message", out var rootMessage) && rootMessage.ValueKind == JsonValueKind.String)
            return rootMessage.GetString();
        return null;
    }

    private async Task<string> ResolveApiKeyAsync(DeepSeekSearchProviderOptions options, CancellationToken signal)
    {
        ThrowIfAborted(signal);
        if (options.ApiKey is { Length: > 0 })
            return options.ApiKey;
        string? resolved = null;
        try
        {
            if (options.ResolveApiKey is not null)
                resolved = await Abortable(options.ResolveApiKey(), signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw SearchAborted(signal);
        }
        catch (Exception error)
        {
            throw new WebError($"DeepSeek search credential resolution failed: {error.Message}", WebErrorCodes.ProviderError, error);
        }
        if (resolved is { Length: > 0 })
            return resolved;
        var reference = "DEEPSEEK_API_KEY";
        throw new WebError(
            $"DeepSeek search has no API key for \"{reference}\"; store it through the credentials service"
            + " (the web Models page writes it), export it in the launching environment, or set a literal"
            + " \"apiKey\" in the web-search-deepseek config",
            WebErrorCodes.CredentialMissing);
    }

    private static async Task<T> Abortable<T>(Task<T> operation, CancellationToken signal)
    {
        if (signal.IsCancellationRequested)
            throw SearchAborted(signal);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(() => tcs.TrySetException(SearchAborted(signal)));
        try
        {
            var result = await operation.ConfigureAwait(false);
            tcs.TrySetResult(result);
        }
        catch (Exception error)
        {
            tcs.TrySetException(error);
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    private static WebError SearchAborted(CancellationToken signal)
        => new("DeepSeek search aborted", WebErrorCodes.Aborted);

    private static void ThrowIfAborted(CancellationToken signal)
    {
        if (signal.IsCancellationRequested)
            throw SearchAborted(signal);
    }

    private static WebError SearchEndpointError(string endpoint, string message, Exception? cause = null)
        => new(
            $"{message}\n\nThe web search request used endpoint {JsonSerializer.Serialize(endpoint)}. "
            + "Search endpoint configuration is separate from chat. If that endpoint is not intended, "
            + "guide the user to Settings > Plugins > Plugin configuration > Web search, where they can "
            + "change and save Endpoint. If that settings page is unavailable, the user can set "
            + "DEEPSEEK_SEARCH_BASE_URL or configure web-search-deepseek.baseURL to a trusted "
            + "Anthropic-compatible Messages API base. Only the user should choose or change the endpoint.",
            WebErrorCodes.ProviderError,
            cause);

    private static bool IsRedirectStatus(int status)
        => status is 301 or 302 or 303 or 307 or 308;
}

public static class WebSearchDeepseek
{
    public static IDisposable Apply(Context ctx, object? config = null)
    {
        var resolved = config switch
        {
            DeepSeekSearchConfig typed => typed,
            null => new DeepSeekSearchConfig(),
            IReadOnlyDictionary<string, object?> dict => FromDictionary(dict),
            _ => throw new ArgumentException("web-search-deepseek config must be an object or DeepSeekSearchConfig", nameof(config)),
        };
        if (resolved.MaxTokens < 1 || resolved.MaxUses < 1)
            throw new ArgumentException("web-search-deepseek: maxTokens and maxUses must be positive integers");
        var web = ctx.Get<WebRuntime>(WebRuntime.ServiceName)
            ?? throw new InvalidOperationException("web service is not registered");
        var options = new DeepSeekSearchProviderOptions
        {
            ApiKey = string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
            BaseUrl = resolved.BaseUrl,
            Model = resolved.Model,
            ApiVersion = resolved.ApiVersion,
            MaxTokens = resolved.MaxTokens,
            MaxUses = resolved.MaxUses,
        };
        return web.RegisterSearchProvider(new DeepSeekSearchProvider(() => options));
    }

    private static DeepSeekSearchConfig FromDictionary(IReadOnlyDictionary<string, object?> dict)
    {
        return new DeepSeekSearchConfig
        {
            ApiKey = dict.GetValueOrDefault("apiKey") as string,
            BaseUrl = dict.GetValueOrDefault("baseURL") as string ?? new DeepSeekSearchConfig().BaseUrl,
            Model = dict.GetValueOrDefault("model") as string ?? new DeepSeekSearchConfig().Model,
            ApiVersion = dict.GetValueOrDefault("apiVersion") as string ?? new DeepSeekSearchConfig().ApiVersion,
            MaxTokens = IntOf(dict, "maxTokens") ?? new DeepSeekSearchConfig().MaxTokens,
            MaxUses = IntOf(dict, "maxUses") ?? new DeepSeekSearchConfig().MaxUses,
        };
    }

    private static int? IntOf(IReadOnlyDictionary<string, object?> dict, string key)
        => dict.GetValueOrDefault(key) switch
        {
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            int value => value,
            double value when double.IsFinite(value) && value % 1 == 0 => (int)value,
            _ => null,
        };
}
