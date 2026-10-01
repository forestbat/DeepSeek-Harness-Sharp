using System.Text;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Llm;

namespace Dsh.Interaction;

/** models.dev 目录里的一条 provider; 协议族与 API 风格由 npm(SDK 方言)决定。 */
public sealed record ProviderCatalogEntry(
    string Id,
    string Name,
    string? BaseUrl,
    IReadOnlyList<string> EnvironmentKeys,
    string? Npm,
    string ModelIdIndex)
{
    private IReadOnlyList<string>? _modelIds;

    /** 模型 id 列表: 目录里以 '\n' 连成一个字符串保存(8 千多个短串会白占 ~8.3k 个对象), 首次访问才切分。 */
    public IReadOnlyList<string> ModelIds
        => _modelIds ??= ModelIdIndex.Length == 0 ? [] : ModelIdIndex.Split('\n');

    /** 模型数量: 不切分即可得到, 供列表/描述显示。 */
    public int ModelCount => ModelIdIndex.Length == 0 ? 0 : ModelIdIndex.Count(character => character == '\n') + 1;

    /** 映射到的落盘 type(canonical); null 表示当前没有适配器能直接服务(需要插件)。 */
    public string? Type => ProviderCatalog.TypeOf(Id, Npm);

    public string Describe()
    {
        if (Type is not { } type)
            return $"{Id}: {Name} — 需插件(npm={Npm ?? "?"})";
        var key = EnvironmentKeys.Count > 0 ? $" key={string.Join('/', EnvironmentKeys)}" : "";
        var models = ModelCount > 0 ? $" models={ModelCount}" : "";
        return $"{Id}: {Name} — type={type}, baseUrl={BaseUrl ?? "-"}{key}{models}";
    }
}

public sealed record ProviderCatalogSnapshot(
    IReadOnlyList<ProviderCatalogEntry> Providers,
    bool FromCache,
    DateTimeOffset? FetchedAt,
    string? Error,
    bool FromEmbedded = false)
{
    public static ProviderCatalogSnapshot Empty(string? error = null) => new([], false, null, error);
}

/**
 * models.dev 目录(与 kilocode 同源: https://models.dev 的 api.json)。
 * 本地缓存 + 离线可用; npm 字段只是"该 provider 用哪个 SDK 方言", 不引入任何 SDK 依赖。
 */
public static class ProviderCatalog
{
    public const string SourceUrl = "https://models.dev/api.json";

    private const string CacheFileName = "models-dev.json";
    private static readonly TimeSpan FreshFor = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string CacheFile(HarnessHome home) => Path.Combine(home.CachePath, CacheFileName);

    /** `--model-ids` 的 <c>&lt;all&gt;</c> 标记: 表示"取目录里该 provider 声明的全部模型", 命令与面板都不必铺开长串模型名。 */
    public const string AllModelsMarker = "<all>";

    public static bool IsAllModels(string? value)
        => value is not null && string.Equals(value.Trim(), AllModelsMarker, StringComparison.OrdinalIgnoreCase);

    /** <c>&lt;all&gt;</c> 展开: 目录里该 id 的全部模型; 目录里没有该 provider 时返回 null, 由调用方报错。 */
    public static IReadOnlyList<string>? AllModelsFor(HarnessHome home, string providerId)
        => LoadCached(home).Providers
            .FirstOrDefault(entry => string.Equals(entry.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?.ModelIds;

    /** 内置映射(优先于 npm 方言): DeepSeek 有专有协议族。 */
    private static readonly Dictionary<string, string> BuiltInTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek"] = ProviderTypes.DeepSeek,
    };

    /** provider 条目 → 落盘 type(canonical); 没有适配器的方言返回 null(需要插件)。 */
    public static string? TypeOf(string id, string? npm)
    {
        if (BuiltInTypes.TryGetValue(id, out var builtIn))
            return builtIn;
        if (string.IsNullOrWhiteSpace(npm))
            return null;
        return npm.Trim() switch
        {
            "@ai-sdk/openai-compatible" => ProviderTypes.OpenAiCompatible,
            "@ai-sdk/openai" => ProviderTypes.OpenAiCompatibleResponses,
            "@ai-sdk/anthropic" => ProviderTypes.AnthropicMessages,
            _ when npm.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase) => ProviderTypes.AnthropicMessages,
            _ => null,
        };
    }

    /** 只读缓存(菜单候选等同步路径用); 没有缓存或缓存损坏时退回内置快照(离线可用)。 */
    public static ProviderCatalogSnapshot LoadCached(HarnessHome home)
    {
        var file = CacheFile(home);
        if (!File.Exists(file))
            return LoadEmbedded();
        try
        {
            var snapshot = Parse(File.ReadAllText(file));
            return snapshot with { FromCache = true, FetchedAt = File.GetLastWriteTimeUtc(file) };
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return LoadEmbedded() with { Error = error.Message };
        }
    }

    /** 内置快照(随程序发布, 只解析一次): 首次运行/无网络/无缓存时保证目录可用。 */
    public static ProviderCatalogSnapshot LoadEmbedded() => Embedded.Value;

    private static readonly Lazy<ProviderCatalogSnapshot> Embedded = new(ParseEmbedded, isThreadSafe: true);

    private static ProviderCatalogSnapshot ParseEmbedded()
    {
        using var stream = typeof(ProviderCatalog).Assembly
            .GetManifestResourceStream("Dsh.Interaction.Resources.models-dev.json");
        if (stream is null)
            return ProviderCatalogSnapshot.Empty("内置 models.dev 快照缺失");
        try
        {
            // 先整体读进 MemoryStream 再按 ReadOnlyMemory 解析: 不经过字符串, 也不让 JsonDocument 去租用并长期持有大缓冲池
            // (实测 5MB 快照若走 JsonDocument.Parse(Stream), 池化缓冲会让进程常驻 ~16.7MB; 换成内存解析后降到 ~1MB)。
            using var buffer = new MemoryStream(stream.CanSeek ? (int)stream.Length : 0);
            stream.CopyTo(buffer);
            return Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)) with { FromEmbedded = true };
        }
        catch (JsonException error)
        {
            return ProviderCatalogSnapshot.Empty($"内置快照解析失败: {error.Message}");
        }
    }

    /** 缓存过期(或强制)时联网拉取并写缓存; 失败时退回已有缓存(附错误说明)。 */
    public static async Task<ProviderCatalogSnapshot> LoadAsync(
        HarnessHome home,
        bool refresh,
        CancellationToken signal = default)
    {
        var cached = LoadCached(home);
        if (!refresh && cached.FetchedAt is { } fetchedAt && DateTimeOffset.UtcNow - fetchedAt < FreshFor)
            return cached;
        try
        {
            var json = await Http.GetStringAsync(SourceUrl, signal);
            var snapshot = Parse(json);
            Directory.CreateDirectory(home.CachePath);
            await File.WriteAllTextAsync(CacheFile(home), json, signal);
            return snapshot with { FetchedAt = DateTimeOffset.UtcNow };
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException)
        {
            if (cached.Providers.Count == 0)
                return cached with { Error = error.Message };
            var source = cached.FromCache ? "缓存" : "内置快照";
            return cached with { Error = $"刷新失败, 仍用{source}: {error.Message}" };
        }
    }

    public static ProviderCatalogSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseDocument(document);
    }

    /** 从内存解析(内置快照 5MB: 不落成字符串, 也不用 JsonDocument 的池化缓冲)。 */
    public static ProviderCatalogSnapshot Parse(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseDocument(document);
    }

    private static ProviderCatalogSnapshot ParseDocument(JsonDocument document)
    {
        // 手写 JsonDocument 解析(不用反射反序列化): 目录字段少, 且裁剪/AOT 发布下不需要 JsonSerializer 元数据。
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return ProviderCatalogSnapshot.Empty("目录为空");
        var providers = new List<ProviderCatalogEntry>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var entry = property.Value;
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            var id = String(entry, "id") ?? property.Name;
            providers.Add(new ProviderCatalogEntry(
                id,
                String(entry, "name") ?? id,
                String(entry, "api"),
                Strings(entry, "env"),
                String(entry, "npm"),
                JoinedKeys(entry, "models")));
        }

        providers.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return new ProviderCatalogSnapshot(providers, false, null, null);
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : [];

    /** 对象键以 '\n' 连成一个串: 目录里 8 千多个模型 id 若逐个建字符串会白占内存与对象数(P8 项)。 */
    private static string JoinedKeys(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return "";
        var builder = new StringBuilder();
        foreach (var property in value.EnumerateObject())
        {
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(property.Name);
        }

        return builder.ToString();
    }
}
