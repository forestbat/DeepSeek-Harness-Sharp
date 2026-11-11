using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Llm.Anthropic;

public sealed class AnthropicAdapter : LlmAdapter
{
    /** 探测端点认不认 `output_config.effort` 时用的取值: 故意非法的串, 网关通常会在报错里列出合法取值。 */
    private const string ProbeEffort = "__dsh_probe__";
    private const int ProbeMaxTokens = 16;

    /** 网关不校验(非法值也 200)时给的档位: Anthropic 族的通用取值。 */
    private static readonly IReadOnlyList<string> StandardEfforts = ["low", "medium", "high", "max", "xhigh"];
    private static readonly TimeSpan ProbeRetryCooldown = TimeSpan.FromMinutes(5);

    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly Func<CancellationToken, Task<LlmCredential>>? _resolveCredential;
    private readonly IReadOnlyList<string> _modelIds;
    private readonly HttpClient _http;
    private readonly IModelReasoningSource _metadataSource;
    private readonly AnthropicModelMetadataSource? _discoveredModels;
    private readonly ModelsDevReasoningSource _modelsDev;
    private readonly ReasoningEffortsCache _effortsCache;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BackgroundRefreshGate> _probeGates =
        new(StringComparer.Ordinal);

    public AnthropicAdapter(
        string providerId,
        string baseUrl,
        string? apiKey = null,
        IReadOnlyList<string>? modelIds = null,
        HttpClient? httpClient = null,
        IModelReasoningSource? metadataSource = null,
        ReasoningEffortsCache? reasoningEfforts = null,
        Func<CancellationToken, Task<LlmCredential>>? resolveCredential = null)
    {
        ProviderInfo = new LlmProviderInfo(providerId, "Anthropic");
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _resolveCredential = resolveCredential;
        _modelIds = modelIds ?? [];
        _http = httpClient ?? new HttpClient();
        _modelsDev = new ModelsDevReasoningSource("anthropic");
        _effortsCache = reasoningEfforts ?? new ReasoningEffortsCache(null);
        if (metadataSource is not null)
        {
            _metadataSource = metadataSource;
        }
        else
        {
            var source = new AnthropicModelMetadataSource(_baseUrl, _apiKey, _http, resolveCredential);
            _metadataSource = source;
            _discoveredModels = source;
            source.RequestRefresh();
        }
    }

    public override LlmProviderInfo ProviderInfo { get; }

    public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, ProviderTypes.AnthropicMessages);

    /** 可列出的模型 = settings 声明/models.dev 目录 ∪ 端点 /v1/models 自报的(中转站只能从端点知道自己的模型)。 */
    public override IReadOnlyList<LlmModelInfo> ListModels()
    {
        // 端点清单是后台拉取的: 之前没成功就在这次重算里再试一次(有冷却), 选单因此能自己恢复。
        _discoveredModels?.RequestRefresh();
        return [.. _modelIds
            .Concat(_discoveredModels?.ModelIds ?? [])
            .Distinct(StringComparer.Ordinal)
            .Select(id => new LlmModelInfo(ProviderInfo.Id, id, id, null, _modelsDev.InputModalitiesFor(id)))];
    }

    /**
     * 推理强度解析顺序: 端点 /v1/models 的 capabilities.effort 实时元数据 → 内置 models.dev 快照 → 端点探测结果。
     * 上下文窗口与输入模态取自 models.dev 快照。
     */
    public override LlmResolvedModelInfo ResolveModel(string model)
    {
        var reasoning = _metadataSource.ReasoningFor(model) ?? _modelsDev.ReasoningFor(model) ?? ReasoningForUnknownModel(model);
        return new LlmResolvedModelInfo(ProviderInfo.Id, model, model, null, _modelsDev.InputModalitiesFor(model),
            _modelsDev.ContextWindowFor(model), null, reasoning);
    }

    /**
     * 端点与目录都没声明推理强度时的退路: 用探测过的结论, 还没探测过就后台问一次。
     * 目录明确收录了该模型(哪怕"不支持推理")时以目录为准, 不去打扰端点。
     */
    private LlmModelReasoningInfo? ReasoningForUnknownModel(string model)
    {
        if (_modelsDev.Knows(model))
            return null;
        if (_effortsCache.TryGet(EffortsKey(model), out var efforts))
            return efforts.Count == 0 ? null : LlmModelReasoningInfo.FromIds(efforts);
        StartReasoningProbe(model);
        return null;
    }

    private string EffortsKey(string model) => ReasoningEffortsCache.Key(ProviderTypes.AnthropicMessages, _baseUrl, model);

    /** 每个模型最多同时跑一次探测; 失败(端点抖动/网络不通)按冷却重试, 结论由缓存负责复用。 */
    private void StartReasoningProbe(string model)
    {
        var gate = _probeGates.GetOrAdd(model, _ => new BackgroundRefreshGate(ProbeRetryCooldown));
        if (!gate.TryBegin())
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await ProbeReasoningEffortAsync(model, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            finally
            {
                gate.End();
            }
        });
    }

    /**
     * 一次最小请求问端点认不认 `output_config.effort`: 网关一般会在报错里列出合法取值;
     * 什么都不校验(非法值也 200)时按族内通用取值给。答案落盘, 重启不再重复问。
     * 探测直接看状态码与报错正文, 不走流式解析: 解析失败不该让"端点接受"这个结论丢失。
     */
    private async Task ProbeReasoningEffortAsync(string model, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = ProbeMaxTokens,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "ping" }),
            ["output_config"] = new JsonObject { ["effort"] = ProbeEffort },
        };
        var credential = await ResolveCredentialAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages");
        ApplyCredential(request, credential);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Headers.TryAddWithoutValidation("user-agent", AppIdentity.Default.UserAgent);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            _effortsCache.Save(EffortsKey(model), StandardEfforts);
            return;
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Contains("effort", StringComparison.OrdinalIgnoreCase))
        {
            // 端点校验了这个参数: 报错里列了合法取值就用它, 没列就记空(别猜)。
            _effortsCache.Save(EffortsKey(model), AnthropicEffortValues.ParseSupported(body));
        }
    }

    public override async IAsyncEnumerable<StreamChunk> Stream(
        GenerateOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var credential = await ResolveCredentialAsync(cancellationToken).ConfigureAwait(false);
        var payload = AnthropicWire.SerializeRequest(options);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages");
        ApplyCredential(request, credential);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("user-agent", AppIdentity.Default.UserAgent);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new LlmException(new LlmFailure(
                $"Anthropic API request to {_baseUrl} failed: {error.Message}",
                LlmFailureCodes.Transport), error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new LlmException(
                    BuildHttpFailure(response, rawResponse, credential),
                    new Exception(rawResponse.Length > 0 ? rawResponse : $"Anthropic HTTP {(int)response.StatusCode}"));
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var chunk in AnthropicSse.Translate(stream, cancellationToken))
                yield return chunk;
        }
    }

    /** Anthropic 的 API Key 走 x-api-key, 账号登录令牌走 x-dsh-auth-token: 两种凭据的请求头不同。 */
    private static void ApplyCredential(HttpRequestMessage request, LlmCredential credential)
    {
        if (credential.Scheme == LlmCredentialScheme.DshAuthToken)
            request.Headers.TryAddWithoutValidation(LlmCredential.DshAuthTokenHeader, credential.Token);
        else
            request.Headers.TryAddWithoutValidation("x-api-key", credential.Token);
    }

    /** 凭据来源: 有注入的解析器就用它(账号令牌走 x-dsh-auth-token), 否则用构造时的 API Key。 */
    private async Task<LlmCredential> ResolveCredentialAsync(CancellationToken cancellationToken)
    {
        if (_resolveCredential is { } resolve)
            return await resolve(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new LlmException(new LlmFailure(
                "Anthropic API key is missing",
                LlmFailureCodes.MissingCredential));
        return new LlmCredential(_apiKey, LlmCredentialScheme.Bearer);
    }

    private LlmFailure BuildHttpFailure(HttpResponseMessage response, string rawResponse, LlmCredential credential)
    {
        var status = (int)response.StatusCode;
        var message = $"Anthropic API error (HTTP {status})";
        string? type = null;
        string? code = null;

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var messageProperty))
                    message = messageProperty.GetString() ?? message;
                if (error.TryGetProperty("type", out var typeProperty))
                    type = typeProperty.GetString();
                if (error.TryGetProperty("code", out var codeProperty))
                    code = codeProperty.GetString();
            }
        }
        catch (JsonException)
        {
        }

        var delay = RetryAfterHeader.ParseMs(
            response.Headers.TryGetValues("retry-after", out var values) ? values.FirstOrDefault() : null,
            DateTimeOffset.UtcNow);
        var requestId = response.Headers.TryGetValues("request-id", out var ids)
                        && ids.FirstOrDefault() is { Length: > 0 } id
            ? ProviderRequestId.Create(id)
            : (ProviderRequestId?)null;

        return new LlmFailure(
            message,
            ErrorCode(status, type, code, message, credential),
            status,
            delay,
            requestId);
    }

    private static string ErrorCode(int status, string? type, string? code, string message, LlmCredential credential)
    {
        if (status is 401 or 403)
            return credential.Scheme == LlmCredentialScheme.DshAuthToken
                ? LlmFailureCodes.AccountTokenInvalid
                : LlmFailureCodes.InvalidCredential;

        var detail = string.Join(' ', new[] { type, code, message }.Where(field => !string.IsNullOrEmpty(field)));

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
}
