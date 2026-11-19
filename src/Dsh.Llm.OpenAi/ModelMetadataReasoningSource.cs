using System.Collections.Concurrent;
using System.Text.Json;

namespace Dsh.Llm.OpenAi;

/**
 * 从 openai 兼容端点的 /models 响应读取每个模型支持的推理强度: 优先 opencode.variants, 其次 supported_parameters 的 reasoning_effort。
 * 同一份响应里还带着该端点提供的全部模型 id, 一并留作模型清单(中转站的模型只能从这里知道)。
 */
public sealed class ModelMetadataReasoningSource : IModelReasoningSource
{
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(10);

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private readonly BackgroundRefreshGate _gate = new(RefreshCooldown);
    private readonly ConcurrentDictionary<string, LlmModelReasoningInfo> _byModel = new(StringComparer.Ordinal);
    /** 端点自报的上下文窗口: 中转站的模型不在 models.dev 目录里, 自动压缩只能问端点要。 */
    private readonly ConcurrentDictionary<string, int> _contextWindows = new(StringComparer.Ordinal);
    /** 上一次探测失败/未声明的时刻(Environment.TickCount64), 0 表示没有记录。 */
    private long _failedAt;
    private volatile IReadOnlyList<string> _modelIds = [];

    public ModelMetadataReasoningSource(string baseUrl, string apiKey, HttpClient http)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _http = http;
    }

    public LlmModelReasoningInfo? ReasoningFor(string model)
        => _byModel.TryGetValue(model, out var reasoning) ? reasoning : null;

    /** 端点自报的模型清单: 拉取完成前与失败时为空。 */
    public IReadOnlyList<string> ModelIds => _modelIds;

    /**
     * 某个模型的上下文窗口: 命中缓存直接返回; 没命中就补拉一次端点清单(受冷却窗口限制, 不会每轮都打网关)。
     * 端点没声明就返回 null —— 调用方据此决定怎么降级, 而不是编一个数字。
     */
    public async Task<int?> EnsureContextWindowAsync(string model, CancellationToken cancellationToken)
    {
        if (_contextWindows.TryGetValue(model, out var known))
            return known;
        // 网关挂住/未声明时的静默期: 探测要等满超时, 不能让每一轮都白等一次。
        if (_failedAt != 0 && Environment.TickCount64 - _failedAt < (long)FailedProbeCooldown.TotalMilliseconds)
            return null;
        if (!_gate.TryBegin())
            return null;
        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _failedAt = Environment.TickCount64;
            return null;
        }
        finally
        {
            _gate.End();
        }
        if (!_contextWindows.TryGetValue(model, out var probed) || probed <= 0)
        {
            _failedAt = Environment.TickCount64;
            return null;
        }
        return probed;
    }

    /** 清单还没拿到时补一次后台拉取(冷却窗口内合并): 端点抖动过之后不必重启进程就能恢复。 */
    public void RequestRefresh()
    {
        if (_modelIds.Count > 0 || !_gate.TryBegin())
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            finally
            {
                _gate.End();
            }
        });
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        foreach (var (model, reasoning) in Parse(json))
            _byModel[model] = reasoning;
        foreach (var (model, window) in ParseContextWindows(json))
            _contextWindows[model] = window;
        _modelIds = EndpointModelList.ParseIds(json);
    }

    /** 端点请求是"有就用"的补充信息, 不能拖住回合: 超时按没声明处理。 */
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /** 探测失败后的静默期。 */
    private static readonly TimeSpan FailedProbeCooldown = TimeSpan.FromMinutes(5);

    /**
     * 从同一份 /models 响应里读上下文窗口, 容忍各网关的不同字段名:
     * context_length / max_context_length / context_window / max_input_tokens, 以及 openrouter 风格的 top_provider.context_length。
     * 输出上限(max_tokens / max_output_tokens)不是上下文窗口, 明确不取。
     */
    public static IReadOnlyDictionary<string, int> ParseContextWindows(string json)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            var id = entry.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            if (string.IsNullOrEmpty(id))
                continue;
            if (FirstPositive(entry, "context_length", "max_context_length", "context_window", "max_input_tokens") is { } window)
                result[id] = window;
            else if (entry.TryGetProperty("top_provider", out var provider) && provider.ValueKind == JsonValueKind.Object
                && FirstPositive(provider, "context_length", "max_context_length") is { } nested)
                result[id] = nested;
        }
        return result;
    }

    private static int? FirstPositive(JsonElement entry, params string[] names)
    {
        foreach (var name in names)
        {
            if (entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var number) && number > 0)
                return number;
        }
        return null;
    }

    public static IReadOnlyDictionary<string, LlmModelReasoningInfo> Parse(string json)
    {
        var result = new Dictionary<string, LlmModelReasoningInfo>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            var id = entry.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            if (string.IsNullOrEmpty(id))
                continue;
            var efforts = ReadVariants(entry) ?? ReadSupportedEfforts(entry);
            if (efforts is { Count: > 0 })
                result[id] = new LlmModelReasoningInfo(efforts, DefaultEffortOf(efforts));
        }
        return result;
    }

    private static List<LlmReasoningEffortInfo>? ReadVariants(JsonElement entry)
    {
        if (!entry.TryGetProperty("opencode", out var opencode) || opencode.ValueKind != JsonValueKind.Object)
            return null;
        if (!opencode.TryGetProperty("variants", out var variants))
            return null;
        if (variants.ValueKind == JsonValueKind.Object)
        {
            var names = variants.EnumerateObject().Select(property => property.Name).ToList();
            return names.Count == 0 ? null : names.Select(ToEffort).ToList();
        }
        if (variants.ValueKind != JsonValueKind.String)
            return null;
        var text = (variants.GetString() ?? "").Trim();
        if (text.StartsWith("@{", StringComparison.Ordinal) && text.EndsWith('}'))
            text = text[2..^1];
        else if (text.StartsWith('{') && text.EndsWith('}'))
            text = text[1..^1];
        var ids = text
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2)[0].Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return ids.Count == 0 ? null : ids.Select(ToEffort).ToList();
    }

    private static List<LlmReasoningEffortInfo>? ReadSupportedEfforts(JsonElement entry)
    {
        if (!entry.TryGetProperty("supported_parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            return null;
        var supported = parameters
            .EnumerateArray()
            .Where(parameter => parameter.ValueKind == JsonValueKind.String)
            .Select(parameter => parameter.GetString() ?? "")
            .ToHashSet(StringComparer.Ordinal);
        if (!supported.Contains("reasoning_effort"))
            return null;
        string[] genericEfforts = ["low", "medium", "high"];
        return genericEfforts.Select(ToEffort).ToList();
    }

    private static LlmReasoningEffortInfo ToEffort(string id)
        => new(ReasoningEffortId.Create(id), $"{char.ToUpperInvariant(id[0])}{id[1..]}");

    private static ReasoningEffortId? DefaultEffortOf(IReadOnlyList<LlmReasoningEffortInfo> efforts)
        => efforts.FirstOrDefault(effort => effort.Id == ReasoningEffortId.Create("medium"))?.Id
            ?? efforts.FirstOrDefault(effort => effort.Id == ReasoningEffortId.Create("high"))?.Id;
}
