using System.Collections.Concurrent;
using System.Text.Json;

namespace Dsh.Llm.OpenAi;

/** 从 openai 兼容端点的 /models 响应读取每个模型支持的推理强度: 优先 opencode.variants, 其次 supported_parameters 的 reasoning_effort。 */
public sealed class ModelMetadataReasoningSource : IModelReasoningSource
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, LlmModelReasoningInfo> _byModel = new(StringComparer.Ordinal);

    public ModelMetadataReasoningSource(string baseUrl, string apiKey, HttpClient http)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _http = http;
    }

    public LlmModelReasoningInfo? ReasoningFor(string model)
        => _byModel.TryGetValue(model, out var reasoning) ? reasoning : null;

    /** 启动预热: 后台拉取, 失败静默(离线或端点不提供元数据时保持回退链)。 */
    public void RefreshInBackground()
        => _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        });

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (model, reasoning) in Parse(json))
            _byModel[model] = reasoning;
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
