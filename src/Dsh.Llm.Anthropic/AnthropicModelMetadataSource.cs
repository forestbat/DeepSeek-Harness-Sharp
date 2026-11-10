using System.Collections.Concurrent;
using System.Text.Json;

namespace Dsh.Llm.Anthropic;

/**
 * 从 Anthropic /v1/models 的 capabilities.effort 读取每个模型支持的推理强度(low/medium/high/max/xhigh)。
 * 同一份响应里还带着该端点提供的全部模型 id, 一并留作模型清单(中转站的模型只能从这里知道)。
 */
public sealed class AnthropicModelMetadataSource : IModelReasoningSource
{
    private static readonly IReadOnlyList<string> EffortOrder = ["low", "medium", "high", "max", "xhigh"];
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(10);

    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly HttpClient _http;
    private readonly BackgroundRefreshGate _gate = new(RefreshCooldown);
    private readonly ConcurrentDictionary<string, LlmModelReasoningInfo> _byModel = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<string> _modelIds = [];

    public AnthropicModelMetadataSource(string baseUrl, string? apiKey, HttpClient http)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _http = http;
    }

    public LlmModelReasoningInfo? ReasoningFor(string model)
        => _byModel.TryGetValue(model, out var reasoning) ? reasoning : null;

    /** 端点自报的模型清单: 拉取完成前与失败(含无 key)时为空。 */
    public IReadOnlyList<string> ModelIds => _modelIds;

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
        if (string.IsNullOrWhiteSpace(_apiKey))
            return;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/models?limit=1000");
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (model, reasoning) in Parse(json))
            _byModel[model] = reasoning;
        _modelIds = EndpointModelList.ParseIds(json);
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
            var efforts = ReadEfforts(entry);
            if (efforts is { Count: > 0 })
                result[id] = new LlmModelReasoningInfo(efforts, DefaultEffortOf(efforts));
        }
        return result;
    }

    private static List<LlmReasoningEffortInfo>? ReadEfforts(JsonElement entry)
    {
        if (!entry.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            return null;
        if (!capabilities.TryGetProperty("effort", out var effort) || effort.ValueKind != JsonValueKind.Object)
            return null;
        var supported = EffortOrder
            .Where(level => effort.TryGetProperty(level, out var levelElement)
                && levelElement.ValueKind == JsonValueKind.Object
                && levelElement.TryGetProperty("supported", out var supportedElement)
                && supportedElement.ValueKind == JsonValueKind.True)
            .ToList();
        return supported.Count == 0 ? null : supported.Select(ToEffort).ToList();
    }

    private static LlmReasoningEffortInfo ToEffort(string id)
        => new(ReasoningEffortId.Create(id), $"{char.ToUpperInvariant(id[0])}{id[1..]}");

    private static ReasoningEffortId? DefaultEffortOf(IReadOnlyList<LlmReasoningEffortInfo> efforts)
        => efforts.FirstOrDefault(effort => effort.Id == ReasoningEffortId.Create("high"))?.Id
            ?? efforts.FirstOrDefault(effort => effort.Id == ReasoningEffortId.Create("medium"))?.Id
            ?? efforts[0].Id;
}
