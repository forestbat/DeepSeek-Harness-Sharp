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
            var snapshot = Parse(ReadCacheTextShared(file));
            return snapshot with { FromCache = true, FetchedAt = File.GetLastWriteTimeUtc(file) };
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return LoadEmbedded() with { Error = error.Message };
        }
    }

    /** 内置快照(随 Dsh.Llm 发布, 只解析一次); 首次运行/无网络/无缓存时保证目录可用。 */
    public static ProviderCatalogSnapshot LoadEmbedded() => Embedded.Value with { FromEmbedded = true };

    private static readonly Lazy<ProviderCatalogSnapshot> Embedded =
        new(() => Map(ModelsDevCatalog.Embedded), isThreadSafe: true);

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
            // 存储根可能已被删除(测试结束清理 / 用户切换 home): 不重建目录, 否则会留下"只剩 cache"的空家目录。
            if (!Directory.Exists(home.Root))
                return snapshot with { FetchedAt = DateTimeOffset.UtcNow };
            Directory.CreateDirectory(home.CachePath);
            await WriteCacheAtomicAsync(CacheFile(home), json, signal);
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

    /** 缓存可能被其它线程/实例同时读写: 共享 ReadWrite|Delete 打开, 避开"文件正在使用"。 */
    private static string ReadCacheTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /** 原子写: 先写同目录临时文件再改名覆盖, 避免并发读到半截内容, 也避免删除目录时与写句柄冲突。 */
    private static async Task WriteCacheAtomicAsync(string path, string content, CancellationToken signal)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, content, signal);
        File.Move(temp, path, overwrite: true);
    }

    public static ProviderCatalogSnapshot Parse(string json)
    {
        try
        {
            return Map(ModelsDevCatalog.FromJson(json));
        }
        catch (JsonException error)
        {
            return ProviderCatalogSnapshot.Empty($"目录解析失败: {error.Message}");
        }
    }

    /** models.dev 目录 → 目录条目(快照解析在低层 Dsh.Llm; 这里只做记录映射)。 */
    private static ProviderCatalogSnapshot Map(ModelsDevCatalog catalog)
        => new(
            [.. catalog.Providers.Select(provider => new ProviderCatalogEntry(
                provider.Id,
                provider.Name,
                provider.BaseUrl,
                provider.EnvironmentKeys,
                provider.Npm,
                provider.ModelIdIndex))],
            false,
            null,
            null);
}
