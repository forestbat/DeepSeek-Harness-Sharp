using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Skills;

/** settings.yaml 的 skills.urls / skills.enabled: 远程技能源, 以及允许进入目录的远程技能名。 */
public sealed record SkillRemoteConfig
{
    public IReadOnlyList<string> Urls { get; init; } = [];

    /** 默认空: 远程技能一律不启用, 只有用户在这里点名才会上架(防止远端提示词注入)。 */
    public IReadOnlyList<string> Enabled { get; init; } = [];

    public string ProviderName { get; init; } = RemoteSkillProvider.DefaultProviderName;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/** 一个远程技能源的内容: 名字 + 说明 + 正文(SKILL.md)。 */
internal sealed record RemoteSkillEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("whenToUse")] string? WhenToUse,
    [property: JsonPropertyName("content")] string Content,
    string BaseUrl);

internal sealed record RemoteSkillIndex([property: JsonPropertyName("skills")] IReadOnlyList<RemoteSkillDocument> Skills);

internal sealed record RemoteSkillDocument(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("whenToUse")] string? WhenToUse,
    [property: JsonPropertyName("content")] string? Content);

/**
 * 从 skills.urls 拉取技能目录。抓到的技能默认**不**进入目录:
 * 只有 skills.enabled 里点名的技能才会被模型看到, 其它一律丢弃 —— 远端内容不进提示词就没有提示词攻击面。
 */
public sealed class RemoteSkillProvider : ISkillProvider, IDisposable
{
    public const string DefaultProviderName = "remote";
    public const string Source = "remote";

    internal const int RemoteSkillRank = 600;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IReadOnlyList<string> _urls;
    private readonly IReadOnlySet<string> _enabled;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private IReadOnlyList<RemoteSkillEntry>? _cache;

    public RemoteSkillProvider(SkillRemoteConfig config, HttpClient? http = null)
    {
        Name = config.ProviderName;
        _urls = [.. config.Urls.Where(url => !string.IsNullOrWhiteSpace(url))];
        _enabled = new HashSet<string>(config.Enabled, StringComparer.Ordinal);
        _timeout = config.Timeout;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
    }

    public string Name { get; }

    /** 已经抓到的远程技能(只含启用的那些), 供诊断/测试核对。 */
    public IReadOnlyList<string> LoadedNames
    {
        get
        {
            lock (_gate)
                return _cache is null ? [] : [.. _cache.Select(entry => entry.Name)];
        }
    }

    public async Task<SkillProviderObservation> List(SkillLookupOptions options)
    {
        if (_urls.Count == 0)
            return SkillProviderObservation.Full([]);
        var entries = await Load(options.Signal).ConfigureAwait(false);
        return SkillProviderObservation.Full([.. entries.Select(entry => new SkillCandidate
        {
            Name = entry.Name,
            Description = entry.Description,
            WhenToUse = entry.WhenToUse,
            Invocation = new SkillInvocationPolicy(true, true),
            Source = Source,
            Provider = Name,
            Rank = RemoteSkillRank,
            Locator = entry,
            ResourceBase = new SkillResourceBase.UrlResource { Url = entry.BaseUrl },
        })]);
    }

    public Task<SkillDefinition?> Get(SkillCandidate candidate, SkillLookupOptions options)
    {
        if (candidate.Locator is not RemoteSkillEntry entry)
            return Task.FromResult<SkillDefinition?>(null);
        return Task.FromResult<SkillDefinition?>(new SkillDefinition
        {
            Name = entry.Name,
            Description = entry.Description,
            WhenToUse = entry.WhenToUse,
            Invocation = new SkillInvocationPolicy(true, true),
            Source = Source,
            Provider = Name,
            ResourceBase = new SkillResourceBase.UrlResource { Url = entry.BaseUrl },
            Content = entry.Content,
        });
    }

    private async Task<IReadOnlyList<RemoteSkillEntry>> Load(CancellationToken signal)
    {
        lock (_gate)
        {
            if (_cache is { } cached)
                return cached;
        }
        var loaded = new List<RemoteSkillEntry>();
        foreach (var url in _urls)
        {
            if (await Fetch(url, signal).ConfigureAwait(false) is { } entries)
                loaded.AddRange(entries);
        }
        lock (_gate)
            _cache = loaded;
        return loaded;
    }

    private async Task<IReadOnlyList<RemoteSkillEntry>?> Fetch(string url, CancellationToken signal)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(signal);
        timeout.CancelAfter(_timeout);
        try
        {
            var index = await _http.GetFromJsonAsync<RemoteSkillIndex>(url, timeout.Token).ConfigureAwait(false);
            return index is null ? null : Accept(url, index.Skills);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            // 远程源不可用不该让整次技能检索失败: 当作没有远程技能。
            return null;
        }
    }

    /** 未在 skills.enabled 里点名的技能一律不返回: 远端内容进不了提示词, 也就没有注入面。 */
    private IReadOnlyList<RemoteSkillEntry> Accept(string url, IReadOnlyList<RemoteSkillDocument> documents)
    {
        var accepted = new List<RemoteSkillEntry>();
        foreach (var document in documents)
        {
            if (document.Name is not { Length: > 0 } name
                || document.Description is not { Length: > 0 } description
                || document.Content is not { Length: > 0 } content
                || !_enabled.Contains(name))
                continue;
            accepted.Add(new RemoteSkillEntry(name, description, document.WhenToUse, content, url));
        }
        return accepted;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
