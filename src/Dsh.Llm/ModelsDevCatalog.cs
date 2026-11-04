using System.Text;
using System.Text.Json;

namespace Dsh.Llm;

/** models.dev 目录里的一条 provider; 模型 id 列表以 '\n' 连成 ModelIdIndex, 首次访问才切分。 */
public sealed record ModelsDevProvider(
    string Id,
    string Name,
    string? BaseUrl,
    IReadOnlyList<string> EnvironmentKeys,
    string? Npm,
    string ModelIdIndex)
{
    private IReadOnlyList<string>? _modelIds;

    public IReadOnlyList<string> ModelIds
        => _modelIds ??= ModelIdIndex.Length == 0 ? [] : ModelIdIndex.Split('\n');

    public int ModelCount => ModelIdIndex.Length == 0 ? 0 : ModelIdIndex.Count(character => character == '\n') + 1;
}

/**
 * models.dev 快照的低层能力源(快照内置于 Dsh.Llm, 上层 ProviderCatalog 复用同一份解析):
 * provider 列表 + 逐模型的推理强度、输入模态与上下文窗口; 快照只解析一次, 不落中间字符串对象。
 */
public sealed class ModelsDevCatalog
{
    private static readonly IReadOnlyList<string> DefaultPreference = ["medium", "high", "low"];

    private readonly IReadOnlyList<ModelsDevProvider> _providers;
    private readonly Dictionary<string, LlmModelReasoningInfo> _reasoning;
    private readonly Dictionary<string, IReadOnlyList<string>> _inputModalities;
    private readonly Dictionary<string, int> _contextWindows;

    private ModelsDevCatalog(
        IReadOnlyList<ModelsDevProvider> providers,
        Dictionary<string, LlmModelReasoningInfo> reasoning,
        Dictionary<string, IReadOnlyList<string>> inputModalities,
        Dictionary<string, int> contextWindows)
    {
        _providers = providers;
        _reasoning = reasoning;
        _inputModalities = inputModalities;
        _contextWindows = contextWindows;
    }

    public IReadOnlyList<ModelsDevProvider> Providers => _providers;

    /** 推理强度: 键为 "provider/model"; provider 用 models.dev 的 provider 键(如 deepseek/anthropic)。 */
    public LlmModelReasoningInfo? ReasoningFor(string provider, string model)
        => _reasoning.TryGetValue($"{provider}/{model}", out var reasoning) ? reasoning : null;

    /** 输入模态(models.dev 的 modalities.input, 如 text/image/pdf): 键同 ReasoningFor; 目录未声明返回 null。 */
    public IReadOnlyList<string>? InputModalitiesFor(string provider, string model)
        => _inputModalities.GetValueOrDefault($"{provider}/{model}");

    /** 上下文窗口 token 数(models.dev 的 limit.context): 键同 ReasoningFor; 目录未声明返回 null(未知)。 */
    public int? ContextWindowFor(string provider, string model)
        => _contextWindows.TryGetValue($"{provider}/{model}", out var contextWindow) ? contextWindow : null;

    /**
     * 把端点的 baseUrl 映射到 models.dev 的 provider 键(models.dev 的 provider.api);
     * 主机 + 端口一致才算命中(默认端口归一化): 目录里没有该端点的中转站返回 null, 不猜。
     */
    public string? ProviderKeyFor(string? baseUrl) => ProviderFor(null, baseUrl)?.Id;

    /** 该 provider 在目录里的模型 id(先按 providerId 精确对 id, 不中再按 baseUrl 端点对 api); 目录里没有就返回空。 */
    public IReadOnlyList<string> ModelIdsFor(string? providerId, string? baseUrl)
        => ProviderFor(providerId, baseUrl)?.ModelIds ?? [];

    private ModelsDevProvider? ProviderFor(string? providerId, string? baseUrl)
    {
        if (providerId is { Length: > 0 } id
            && _providers.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)) is { } byId)
        {
            return byId;
        }
        var endpoint = EndpointOf(baseUrl);
        if (endpoint is null)
            return null;
        return _providers.FirstOrDefault(provider => string.Equals(EndpointOf(provider.BaseUrl), endpoint, StringComparison.OrdinalIgnoreCase));
    }

    /** 端点标识(host:port, 默认端口归一化到 80/443): 只看主机名会让 127.0.0.1 上的自建端点误撞目录里的本地 runtime。 */
    private static string? EndpointOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return null;
        var port = uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80) : uri.Port;
        return $"{uri.Host.ToLowerInvariant()}:{port}";
    }

    public static ModelsDevCatalog Embedded { get; } = new Lazy<ModelsDevCatalog>(LoadEmbedded, isThreadSafe: true).Value;

    public static ModelsDevCatalog FromJson(string json) => Parse(Encoding.UTF8.GetBytes(json));

    public static ModelsDevCatalog FromBytes(ReadOnlyMemory<byte> json) => Parse(json);

    private static ModelsDevCatalog LoadEmbedded()
    {
        using var stream = typeof(ModelsDevCatalog).Assembly
            .GetManifestResourceStream("Dsh.Llm.Resources.models-dev.json");
        if (stream is null)
            return new ModelsDevCatalog([], [], [], []);
        using var buffer = new MemoryStream(stream.CanSeek ? (int)stream.Length : 0);
        stream.CopyTo(buffer);
        return Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
    }

    private static ModelsDevCatalog Parse(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return new ModelsDevCatalog([], [], [], []);
        var providers = new List<ModelsDevProvider>();
        var reasoning = new Dictionary<string, LlmModelReasoningInfo>(StringComparer.Ordinal);
        var inputModalities = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var contextWindows = new Dictionary<string, int>(StringComparer.Ordinal);
        var modalitySets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var entry = property.Value;
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            var id = String(entry, "id") ?? property.Name;
            var modelIndex = new StringBuilder();
            if (entry.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
            {
                foreach (var model in models.EnumerateObject())
                {
                    if (modelIndex.Length > 0)
                        modelIndex.Append('\n');
                    modelIndex.Append(model.Name);
                    if (ReasoningOf(model.Value) is { } info)
                        reasoning[$"{id}/{model.Name}"] = info;
                    if (InputModalitiesOf(model.Value, modalitySets) is { } modalities)
                        inputModalities[$"{id}/{model.Name}"] = modalities;
                    if (ContextWindowOf(model.Value) is { } contextWindow)
                        contextWindows[$"{id}/{model.Name}"] = contextWindow;
                }
            }
            providers.Add(new ModelsDevProvider(
                id,
                String(entry, "name") ?? id,
                String(entry, "api"),
                Strings(entry, "env"),
                String(entry, "npm"),
                modelIndex.ToString()));
        }
        providers.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return new ModelsDevCatalog(providers, reasoning, inputModalities, contextWindows);
    }

    /** 同一组模态在目录里反复出现, 按内容共享同一个列表实例。 */
    private static IReadOnlyList<string>? InputModalitiesOf(JsonElement model, Dictionary<string, IReadOnlyList<string>> shared)
    {
        if (model.ValueKind != JsonValueKind.Object
            || !model.TryGetProperty("modalities", out var modalities)
            || modalities.ValueKind != JsonValueKind.Object)
            return null;
        var input = Strings(modalities, "input");
        if (input.Count == 0)
            return null;
        var key = string.Join(',', input);
        if (!shared.TryGetValue(key, out var list))
            shared[key] = list = input;
        return list;
    }

    /** limit.context 是模型的上下文窗口 token 数; 缺失或非正数按未知处理。 */
    private static int? ContextWindowOf(JsonElement model)
        => model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("limit", out var limit)
            && limit.ValueKind == JsonValueKind.Object
            && limit.TryGetProperty("context", out var context)
            && context.ValueKind == JsonValueKind.Number
            && context.TryGetInt32(out var tokens)
            && tokens > 0
            ? tokens
            : null;

    private static LlmModelReasoningInfo? ReasoningOf(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object)
            return null;
        if (!model.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind != JsonValueKind.True)
            return null;
        var efforts = new List<LlmReasoningEffortInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id)
        {
            if (id.Length > 0 && seen.Add(id))
                efforts.Add(new LlmReasoningEffortInfo(ReasoningEffortId.Create(id), $"{char.ToUpperInvariant(id[0])}{id[1..]}"));
        }
        if (model.TryGetProperty("reasoning_options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                switch (String(option, "type"))
                {
                    case "toggle":
                        Add("off");
                        break;
                    case "effort" when option.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array:
                        foreach (var value in values.EnumerateArray())
                        {
                            if (value.ValueKind == JsonValueKind.String)
                                Add(value.GetString()!);
                        }
                        break;
                }
            }
        }
        var declared = String(model, "reasoning_default") ?? String(model, "default_reasoning");
        ReasoningEffortId? defaultEffort = null;
        if (declared is not null && seen.Contains(declared))
            defaultEffort = ReasoningEffortId.Create(declared);
        else
            defaultEffort = DefaultPreference
                .Where(candidate => seen.Contains(candidate))
                .Select(ReasoningEffortId.Create)
                .FirstOrDefault();
        if (defaultEffort is null && efforts.Count > 0)
            defaultEffort = efforts[0].Id;
        return new LlmModelReasoningInfo(efforts, defaultEffort);
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : [];
}
