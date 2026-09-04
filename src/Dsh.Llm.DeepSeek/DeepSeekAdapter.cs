using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm.DeepSeek;

public sealed record DeepSeekCatalogModel(
    string Id,
    string? Name = null,
    string? Description = null,
    int? ContextWindow = null,
    int? MaxTokens = null,
    IReadOnlyList<string>? InputModalities = null);

public sealed record DeepSeekConnectionOptions(
    string BaseUrl,
    string ApiKeyEnv,
    RequestDefaults Defaults,
    int MaxTokens,
    int DefaultContextWindow,
    IReadOnlyList<DeepSeekCatalogModel> Models,
    long StreamIdleTimeoutMs,
    ResolvedRetryPolicy RetryPolicy)
{
    public const long DefaultStreamIdleTimeoutMs = 300_000;
    public const int DefaultContextWindowValue = 1_000_000;
    public const int DefaultMaxTokens = 256_000;
}

public sealed class DeepSeekAdapterOptions
{
    public required Func<DeepSeekConnectionOptions> Options { get; init; }
    public required Func<DeepSeekConnectionOptions, CancellationToken, Task<string>> ResolveApiKey { get; init; }
    public required Func<string> ResolveUserId { get; init; }
    public Func<IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task>? PrepareExtensions { get; init; }
    public HttpClient? HttpClient { get; init; }
}

public sealed class DeepSeekAdapter : LlmAdapter
{
    public const string StreamIdleTimeoutCode = "LLM_STREAM_IDLE_TIMEOUT";

    private static readonly ReasoningEffortId OffEffort = ReasoningEffortId.Create("off");
    private static readonly ReasoningEffortId LowEffort = ReasoningEffortId.Create("low");
    private static readonly ReasoningEffortId HighEffort = ReasoningEffortId.Create("high");
    private static readonly ReasoningEffortId MaxEffort = ReasoningEffortId.Create("max");

    private static readonly IReadOnlyList<LlmReasoningEffortInfo> ReasoningEfforts =
    [
        new(OffEffort, "Off", "Use for simple tasks that do not need reasoning."),
        new(LowEffort, "Low", "Prefer for routine or latency-sensitive tasks."),
        new(HighEffort, "High", "The default balance for most tasks."),
        new(MaxEffort, "Max", "Reserve for the hardest quality-first tasks."),
    ];

    private static readonly IReadOnlyList<LlmReasoningEffortInfo> OffOnlyReasoningEfforts =
    [
        new(OffEffort, "Off", "Use for simple tasks that do not need reasoning."),
    ];

    private readonly DeepSeekAdapterOptions _config;
    private readonly HttpClient _http;

    public DeepSeekAdapter(string providerId, DeepSeekAdapterOptions config)
    {
        ProviderInfo = new LlmProviderInfo(providerId, "DeepSeek");
        _config = config;
        _http = config.HttpClient ?? new HttpClient();
    }

    public override LlmProviderInfo ProviderInfo { get; }

    public override ResolvedRetryPolicy ProviderRetryPolicy => _config.Options().RetryPolicy;

    public override IReadOnlyList<LlmModelInfo> ListModels()
        => _config.Options().Models
            .Select(model => new LlmModelInfo(
                ProviderInfo.Id,
                model.Id,
                model.Name ?? model.Id,
                model.Description,
                model.InputModalities ?? ["text"]))
            .ToList();

    public override LlmResolvedModelInfo? ResolveModel(string model)
    {
        var connection = _config.Options();
        var configured = connection.Models.FirstOrDefault(entry => entry.Id == model);
        var contextWindow = configured?.ContextWindow ?? connection.DefaultContextWindow;
        return new LlmResolvedModelInfo(
            ProviderInfo.Id,
            model,
            configured?.Name ?? model,
            configured?.Description,
            configured?.InputModalities ?? ["text"],
            contextWindow,
            configured?.MaxTokens ?? connection.MaxTokens,
            connection.Defaults.Thinking == "disabled"
                ? new LlmModelReasoningInfo(OffOnlyReasoningEfforts, OffEffort)
                : new LlmModelReasoningInfo(ReasoningEfforts, connection.Defaults.ReasoningEffort switch
                {
                    "off" => OffEffort,
                    "low" => LowEffort,
                    "max" => MaxEffort,
                    _ => HighEffort,
                }));
    }

    public override PreparedAdapterCall PrepareCall(string model, CancellationToken cancellationToken)
    {
        var connection = _config.Options();
        return new PreparedAdapterCall(model, (options, ct) => StreamWithConnection(options, connection, ct));
    }

    public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
        => StreamWithConnection(options, _config.Options(), cancellationToken);

    private async IAsyncEnumerable<StreamChunk> StreamWithConnection(
        GenerateOptions options,
        DeepSeekConnectionOptions connection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (options.Messages.Any(message => message.Content.Any(block => block is ImageBlock)))
        {
            var model = connection.Models.FirstOrDefault(entry => entry.Id == options.Model);
            if (model?.InputModalities?.Contains("image") != true)
            {
                throw new LlmException(new LlmFailure(
                    $"DeepSeek model \"{options.Model}\" does not accept image input.",
                    "UNSUPPORTED_CONTENT"));
            }
            throw new LlmException(new LlmFailure(
                "DeepSeek image conversion requires the durable attachment service.",
                "UNSUPPORTED_CONTENT"));
        }

        var apiKey = await _config.ResolveApiKey(connection, cancellationToken);
        using var watchdog = new IdleWatchdog(cancellationToken, connection.StreamIdleTimeoutMs);

        IAsyncEnumerator<StreamChunk> iterator;
        try
        {
            iterator = Request(options, watchdog.Token, connection, apiKey, watchdog.Pulse).GetAsyncEnumerator(watchdog.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new LlmException(new LlmFailure("DeepSeek request aborted by caller", "ABORTED"));
        }

        await using (iterator)
        {
            while (true)
            {
                bool moved;
                StreamChunk? current = null;
                LlmException? failure = null;
                try
                {
                    moved = await iterator.MoveNextAsync();
                    if (moved)
                        current = iterator.Current;
                }
                catch (LlmException error)
                {
                    failure = error;
                    moved = false;
                }
                catch (OperationCanceledException error)
                {
                    if (watchdog.TimedOut)
                    {
                        failure = new LlmException(new LlmFailure(
                            $"DeepSeek stream idle timeout after {connection.StreamIdleTimeoutMs}ms",
                            LlmFailureCodes.Timeout), error);
                    }
                    else if (cancellationToken.IsCancellationRequested)
                    {
                        failure = new LlmException(new LlmFailure("DeepSeek request aborted by caller", "ABORTED"), error);
                    }
                    else
                    {
                        failure = new LlmException(new LlmFailure(
                            $"DeepSeek API stream from {connection.BaseUrl} failed: {error.Message}",
                            LlmFailureCodes.Transport), error);
                    }
                    moved = false;
                }
                catch (Exception error)
                {
                    failure = new LlmException(new LlmFailure(
                        $"DeepSeek API stream from {connection.BaseUrl} failed: {error.Message}",
                        LlmFailureCodes.Transport), error);
                    moved = false;
                }
                if (failure is not null)
                    throw failure;
                if (!moved)
                    yield break;
                yield return current!;
            }
        }
    }

    private async IAsyncEnumerable<StreamChunk> Request(
        GenerateOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        DeepSeekConnectionOptions connection,
        string apiKey,
        Action onActivity)
    {
        var body = WireSerialize.SerializeRequest(options, connection.Defaults);
        if (_config.PrepareExtensions is { } prepareExtensions)
        {
            var extensionFields = new Dictionary<string, JsonElement>();
            await prepareExtensions(extensionFields, cancellationToken);
            if (extensionFields.Count > 0)
                throw new LlmException(new LlmFailure(
                    "DeepSeek request extensions are not yet supported by the C# adapter.",
                    "REQUEST_EXTENSION"));
        }
        var payload = JsonSerializer.Serialize(body, DeepSeekJson.Options);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{connection.BaseUrl}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("user-agent", AppIdentity.Default.UserAgent);
        request.Headers.TryAddWithoutValidation("x-deepseek-harness-user-id", _config.ResolveUserId());
        if (options.SessionId is { } sessionId)
            request.Headers.TryAddWithoutValidation("x-deepseek-harness-session-id", sessionId.Value);
        if (options.Purpose is GeneratePurpose.Compaction)
            request.Headers.TryAddWithoutValidation("x-deepseek-harness-compact", "1");
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception error) when (error is not LlmException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new LlmException(new LlmFailure(
                $"DeepSeek API request to {connection.BaseUrl} failed: {error.Message}",
                LlmFailureCodes.Transport), error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                WireErrorBody? providerError = null;
                var message = $"DeepSeek API error (HTTP {(int)response.StatusCode})";
                try
                {
                    providerError = JsonSerializer.Deserialize<WireError>(rawResponse, DeepSeekJson.Options)?.Error;
                    if (providerError?.Message is { } providerMessage)
                        message = providerMessage;
                }
                catch (JsonException)
                {
                    // The HTTP status remains authoritative when a gateway returns malformed JSON.
                }
                var delay = ProviderRetryAfterMs(response.Headers.TryGetValues("retry-after", out var values)
                    ? values.FirstOrDefault() : null);
                var requestId = response.Headers.TryGetValues("x-request-id", out var ids) && ids.FirstOrDefault() is { Length: > 0 } id
                    ? ProviderRequestId.Create(id)
                    : response.Headers.TryGetValues("x-deepseek-request-id", out var dsIds) && dsIds.FirstOrDefault() is { Length: > 0 } dsId
                        ? ProviderRequestId.Create(dsId)
                        : (ProviderRequestId?)null;
                throw new LlmException(
                    new LlmFailure(
                        message,
                        HttpErrorCode((int)response.StatusCode, providerError),
                        (int)response.StatusCode,
                        delay,
                        requestId),
                    new Exception(rawResponse.Length > 0 ? rawResponse : $"DeepSeek HTTP {(int)response.StatusCode}"));
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var chunk in WireTranslate.Translate(SseParser.Parse(stream, _ => onActivity(), cancellationToken), cancellationToken))
                yield return chunk;
        }
    }

    public static string HttpErrorCode(int status, WireErrorBody? error)
    {
        if (status is 401 or 403)
            return "AUTH";
        if (status == 413)
            return "INVALID_REQUEST";
        var detail = string.Join(' ', new[] { error?.Code, error?.Type, error?.Message }.Where(field => !string.IsNullOrEmpty(field)));
        if (LlmFailureClassifiers.IsQuotaExceededError(detail))
            return LlmFailureCodes.Quota;
        if (status == 429)
            return LlmFailureCodes.RateLimit;
        if (status == 400)
        {
            if (LlmFailureClassifiers.IsContextWindowExceededError(detail))
                return LlmFailureCodes.ContextWindowExceeded;
            return "INVALID_REQUEST";
        }
        if (status >= 500)
            return LlmFailureCodes.Server;
        return $"HTTP_{status}";
    }

    private static long? ProviderRetryAfterMs(string? value)
    {
        if (value is null)
            return null;
        if (long.TryParse(value, out var seconds))
        {
            var delay = seconds * 1_000;
            return delay > 0 ? delay : null;
        }
        if (DateTimeOffset.TryParse(value, out var date))
        {
            var delay = (long)(date - DateTimeOffset.UtcNow).TotalMilliseconds;
            return delay > 0 ? delay : null;
        }
        return null;
    }

    private sealed class IdleWatchdog : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private readonly Timer _timer;
        private readonly long _timeoutMs;

        public IdleWatchdog(CancellationToken upstream, long timeoutMs)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(upstream);
            _timeoutMs = timeoutMs;
            _timer = new Timer(_ =>
            {
                TimedOut = true;
                _source.Cancel();
            }, null, timeoutMs, System.Threading.Timeout.Infinite);
        }

        public CancellationToken Token => _source.Token;

        public bool TimedOut { get; private set; }

        public void Pulse() => _timer.Change(_timeoutMs, System.Threading.Timeout.Infinite);

        public void Dispose()
        {
            _timer.Dispose();
            _source.Dispose();
        }
    }
}

public static class DeepSeekJson
{
    public static readonly JsonSerializerOptions Options = DshJson.Options;
}
