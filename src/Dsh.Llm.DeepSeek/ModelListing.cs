using System.Collections.Concurrent;
using System.Text.Json;

namespace Dsh.Llm.DeepSeek;

/** 端点 `GET /models` 声明的一个模型: 能力全部以服务端为准, 不在本地抄一份。 */
public sealed record DeepSeekListedModel(
    string Id,
    string? Name = null,
    int? ContextWindow = null,
    int? MaxOutputTokens = null,
    IReadOnlyList<string>? InputModalities = null,
    IReadOnlyList<string>? Efforts = null,
    string? DefaultEffort = null);

/**
 * DeepSeek 端点模型清单: 账号令牌与 API Key 都要向 `/models` 问, 服务端下发的才是这个凭据当前能用的模型。
 * 拉取在后台进行, 失败保留上一份清单(还没拿到过就是空), 冷却窗口内合并重复请求。
 */
public sealed class DeepSeekModelListing
{
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<LlmCredential>> _resolveCredential;
    private readonly string _listingUrl;
    private readonly HttpClient _http;
    private readonly BackgroundRefreshGate _gate = new(RefreshCooldown);
    private readonly ConcurrentDictionary<string, DeepSeekListedModel> _byId = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<DeepSeekListedModel> _entries = [];

    public DeepSeekModelListing(string baseUrl, Func<CancellationToken, Task<LlmCredential>> resolveCredential, HttpClient http)
    {
        _listingUrl = $"{new Uri(baseUrl).GetLeftPart(UriPartial.Authority)}/models";
        _resolveCredential = resolveCredential;
        _http = http;
    }

    /** 已知清单(无网络): 清单已到位, 因此不会再向端点拉取。 */
    public DeepSeekModelListing(IReadOnlyList<DeepSeekListedModel> entries)
    {
        _listingUrl = "";
        _resolveCredential = _ => throw new InvalidOperationException("a preset model listing has no endpoint to query");
        _http = new HttpClient();
        foreach (var entry in entries)
            _byId[entry.Id] = entry;
        _entries = entries;
    }

    /** 端点声明的完整条目; 还没拉到时为空。 */
    public IReadOnlyList<DeepSeekListedModel> Entries => _entries;

    public DeepSeekListedModel? Find(string model)
        => _byId.TryGetValue(model, out var listed) ? listed : null;

    /** 清单没拿到时补一次后台拉取(冷却窗口内合并): 端点抖动过之后不必重启进程就能恢复。 */
    public void RequestRefresh()
    {
        if (_entries.Count > 0 || !_gate.TryBegin())
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
        var credential = await _resolveCredential(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, _listingUrl);
        credential.Apply(request);
        request.Headers.TryAddWithoutValidation("user-agent", AppIdentity.Default.UserAgent);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var entries = Parse(json);
        foreach (var entry in entries)
            _byId[entry.Id] = entry;
        if (entries.Count > 0)
            _entries = entries;
    }

    /** 逐条目解码: 缺 id 或重复的跳过, 其余字段缺省即未知(不猜)。 */
    public static IReadOnlyList<DeepSeekListedModel> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            return [];
        var entries = new List<DeepSeekListedModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || String(entry, "id") is not { Length: > 0 } id || !seen.Add(id))
                continue;
            var effort = entry.TryGetProperty("effort", out var effortElement) && effortElement.ValueKind == JsonValueKind.Object
                ? effortElement
                : default;
            entries.Add(new DeepSeekListedModel(
                id,
                String(entry, "name"),
                Int(entry, "context_window"),
                Int(entry, "max_output_tokens"),
                Strings(entry, "input_modalities"),
                effort.ValueKind == JsonValueKind.Object ? Strings(effort, "supported_levels") : null,
                effort.ValueKind == JsonValueKind.Object ? String(effort, "default_level") : null));
        }
        return entries;
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0
            ? number
            : null;

    private static IReadOnlyList<string>? Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;
        var items = value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(item => item.Length > 0)
            .ToList();
        return items.Count == 0 ? null : items;
    }
}