using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm.DeepSeek;

public sealed record DeepSeekConnectionOptions(
    string BaseUrl,
    string ApiKeyEnv,
    RequestDefaults Defaults,
    int MaxTokens,
    int DefaultContextWindow,
    IReadOnlyList<ProviderModelSpec> DeclaredModels,
    DeepSeekModelListing Listing,
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
    public required Func<DeepSeekConnectionOptions, CancellationToken, Task<LlmCredential>> ResolveCredential { get; init; }
    public required Func<string> ResolveUserId { get; init; }
    /** 账号令牌被端点拒绝时清登录态; 没有账号来源时为 null。 */
    public Action<LlmCredential>? RejectCredential { get; init; }
    public Func<IDictionary<string, JsonElement>, CancellationToken, Task>? PrepareExtensions { get; init; }
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

    private readonly ModelsDevReasoningSource _modelsDev;

    private readonly DeepSeekAdapterOptions _config;
    private readonly HttpClient _http;

    public DeepSeekAdapter(string providerId, DeepSeekAdapterOptions config)
    {
        ProviderInfo = new LlmProviderInfo(providerId, "DeepSeek");
        _config = config;
        _http = config.HttpClient ?? new HttpClient();
        _modelsDev = new ModelsDevReasoningSource("deepseek");
    }

    public override LlmProviderInfo ProviderInfo { get; }

    public override ResolvedRetryPolicy ProviderRetryPolicy => _config.Options().RetryPolicy;

    /**
     * 可列出的模型 = settings 声明的 ∪ 端点 /models 自报的。
     * 端点清单是这个凭据当前真正能用的模型(可能按账号灰度), 不能写死在本地。
     */
    public override IReadOnlyList<LlmModelInfo> ListModels()
    {
        var connection = _config.Options();
        // 清单是后台拉的: 之前没成功就在这次重算里再试一次(有冷却), 选单因此能自己恢复。
        connection.Listing.RequestRefresh();
        var discovered = connection.Listing.Entries;
        var ids = connection.DeclaredModels.Select(model => model.Id)
            .Concat(discovered.Select(model => model.Id))
            .Distinct(StringComparer.Ordinal);
        return [.. ids.Select(id => new LlmModelInfo(
            ProviderInfo.Id,
            id,
            NameOf(connection, id),
            null,
            InputModalitiesOf(connection, id)))];
    }

    private static string NameOf(DeepSeekConnectionOptions connection, string model)
        => connection.DeclaredModels.FirstOrDefault(entry => entry.Id == model)?.Name
            ?? connection.Listing.Find(model)?.Name
            ?? model;

    /**
     * 输入模态的取值顺序: 端点 /models > models.dev 快照 > 纯文本。
     * 端点最懂自己的凭据能看到什么; 漏掉图片能力会把用户贴的图静默换成一段说明文字。
     */
    private IReadOnlyList<string> InputModalitiesOf(DeepSeekConnectionOptions connection, string model)
        => connection.Listing.Find(model)?.InputModalities
            ?? _modelsDev.InputModalitiesFor(model)
            ?? ["text"];

    public override LlmResolvedModelInfo ResolveModel(string model)
    {
        var connection = _config.Options();
        var configured = connection.DeclaredModels.FirstOrDefault(entry => entry.Id == model);
        if (configured?.SystemPromptUpdate is { } systemPromptUpdate
            && systemPromptUpdate != SystemPromptUpdateModes.InHistory)
        {
            throw new LlmException(new LlmFailure(
                $"catalog model \"{model}\" systemPromptUpdate must be \"{SystemPromptUpdateModes.InHistory}\" when present",
                "INVALID_MODEL_INFO"));
        }
        var listed = connection.Listing.Find(model);
        return new LlmResolvedModelInfo(
            ProviderInfo.Id,
            model,
            NameOf(connection, model),
            null,
            InputModalitiesOf(connection, model),
            listed?.ContextWindow ?? connection.DefaultContextWindow,
            listed?.MaxOutputTokens ?? connection.MaxTokens,
            ReasoningOf(listed) ?? _modelsDev.ReasoningFor(model) ?? DefaultReasoning(connection),
            // DeepSeek 接受对话中途的 system 消息, 变化后的系统提示叠到尾部即可保住前缀缓存;
            // 没显式声明时按 in-history 走, 否则每次变化都会原地改写前缀。
            configured?.SystemPromptUpdate ?? SystemPromptUpdateModes.InHistory);
    }

    /** 端点声明的档位(含"off"表示可关思考)优先; 端点没说就问 models.dev, 再没有就按部署默认。 */
    private static LlmModelReasoningInfo? ReasoningOf(DeepSeekListedModel? listed)
    {
        if (listed?.Efforts is not { Count: > 0 } efforts)
            return null;
        var info = LlmModelReasoningInfo.FromIds(efforts);
        return listed.DefaultEffort is { Length: > 0 } declared
            && efforts.Contains(declared, StringComparer.Ordinal)
                ? info with { DefaultEffort = ReasoningEffortId.Create(declared) }
                : info;
    }

    private static LlmModelReasoningInfo DefaultReasoning(DeepSeekConnectionOptions connection)
        => connection.Defaults.Thinking == "disabled"
            ? new LlmModelReasoningInfo(OffOnlyReasoningEfforts, OffEffort)
            : new LlmModelReasoningInfo(ReasoningEfforts, connection.Defaults.ReasoningEffort switch
            {
                "off" => OffEffort,
                "low" => LowEffort,
                "max" => MaxEffort,
                _ => HighEffort,
            });

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
        if (options.Messages.Any(message => ToolResultImages.Any(message.Content))
            && !InputModalitiesOf(connection, options.Model).Contains("image"))
        {
            throw new LlmException(new LlmFailure(
                $"DeepSeek model \"{options.Model}\" does not accept image input.",
                "UNSUPPORTED_CONTENT"));
        }

        var credential = await _config.ResolveCredential(connection, cancellationToken);
        using var watchdog = new IdleWatchdog(cancellationToken, connection.StreamIdleTimeoutMs);

        IAsyncEnumerator<StreamChunk> iterator;
        try
        {
            iterator = Request(options, watchdog.Token, connection, credential, watchdog.Pulse).GetAsyncEnumerator(watchdog.Token);
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
        LlmCredential credential,
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
        credential.Apply(request);
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
                throw new LlmException(await BuildHttpFailureAsync(response, rawResponse, credential), 
                    new Exception(rawResponse.Length > 0 ? rawResponse : $"DeepSeek HTTP {(int)response.StatusCode}"));
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var chunk in WireTranslate.Translate(SseParser.Parse(stream, _ => onActivity(), cancellationToken), cancellationToken))
                yield return chunk;
        }
    }

    /** 401 与 403 区分凭据类型: 账号令牌被拒是"要重新登录", 不能混成 API Key 无效。 */
    private async Task<LlmFailure> BuildHttpFailureAsync(HttpResponseMessage response, string rawResponse, LlmCredential credential)
    {
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
        var status = (int)response.StatusCode;
        if (status is 401 or 403 && credential.Scheme == LlmCredentialScheme.DshAuthToken)
        {
            _config.RejectCredential?.Invoke(credential);
            return new LlmFailure(
                $"{message}（DeepSeek 账号登录态已失效，请重新登录）",
                LlmFailureCodes.AccountTokenInvalid,
                status);
        }
        var delay = RetryAfterHeader.ParseMs(
            response.Headers.TryGetValues("retry-after", out var values) ? values.FirstOrDefault() : null,
            DateTimeOffset.UtcNow);
        var requestId = response.Headers.TryGetValues("x-request-id", out var ids) && ids.FirstOrDefault() is { Length: > 0 } id
            ? ProviderRequestId.Create(id)
            : response.Headers.TryGetValues("x-deepseek-request-id", out var dsIds) && dsIds.FirstOrDefault() is { Length: > 0 } dsId
                ? ProviderRequestId.Create(dsId)
                : (ProviderRequestId?)null;
        return new LlmFailure(message, HttpErrorCode(status, providerError), status, delay, requestId);
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
            }, null, timeoutMs, Timeout.Infinite);
        }

        public CancellationToken Token => _source.Token;

        public bool TimedOut { get; private set; }

        public void Pulse() => _timer.Change(_timeoutMs, Timeout.Infinite);

        public void Dispose()
        {
            _timer.Dispose();
            _source.Dispose();
        }
    }
}

public static class DeepSeekJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = DeepSeekWireJsonContext.Default,
    };
}