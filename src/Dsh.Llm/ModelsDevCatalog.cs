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
 * provider 列表 + 逐模型的推理强度; 快照只解析一次, 不落中间字符串对象。
 */
public sealed class ModelsDevCatalog
{
    private static readonly IReadOnlyList<string> DefaultPreference = ["medium", "high", "low"];

    private readonly IReadOnlyList<ModelsDevProvider> _providers;
    private readonly Dictionary<string, LlmModelReasoningInfo> _reasoning;

    private ModelsDevCatalog(IReadOnlyList<ModelsDevProvider> providers, Dictionary<string, LlmModelReasoningInfo> reasoning)
    {
        _providers = providers;
        _reasoning = reasoning;
    }

    public IReadOnlyList<ModelsDevProvider> Providers => _providers;

    /** 推理强度: 键为 "provider/model"; provider 用 models.dev 的 provider 键(如 deepseek/anthropic)。 */
    public LlmModelReasoningInfo? ReasoningFor(string provider, string model)
        => _reasoning.TryGetValue($"{provider}/{model}", out var reasoning) ? reasoning : null;

    /**
     * 把端点的 baseUrl 主机名映射到 models.dev 的 provider 键(models.dev 的 provider.api);
     * 目录里没有该域名的中转站返回 null, 不猜。
     */
    public string? ProviderKeyFor(string? baseUrl)
    {
        var host = HostOf(baseUrl);
        if (host is null)
            return null;
        return _providers.FirstOrDefault(provider => string.Equals(HostOf(provider.BaseUrl), host, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static string? HostOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;

    public static ModelsDevCatalog Embedded { get; } = new Lazy<ModelsDevCatalog>(LoadEmbedded, isThreadSafe: true).Value;

    public static ModelsDevCatalog FromJson(string json) => Parse(Encoding.UTF8.GetBytes(json));

    public static ModelsDevCatalog FromBytes(ReadOnlyMemory<byte> json) => Parse(json);

    private static ModelsDevCatalog LoadEmbedded()
    {
        using var stream = typeof(ModelsDevCatalog).Assembly
            .GetManifestResourceStream("Dsh.Llm.Resources.models-dev.json");
        if (stream is null)
            return new ModelsDevCatalog([], []);
        using var buffer = new MemoryStream(stream.CanSeek ? (int)stream.Length : 0);
        stream.CopyTo(buffer);
        return Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
    }

    private static ModelsDevCatalog Parse(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return new ModelsDevCatalog([], []);
        var providers = new List<ModelsDevProvider>();
        var reasoning = new Dictionary<string, LlmModelReasoningInfo>(StringComparer.Ordinal);
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
        return new ModelsDevCatalog(providers, reasoning);
    }

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
