using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.IdeHistory;

public sealed class VsCodeLocalHistoryProvider : IIdeHistoryProvider
{
    private static readonly string[] Products = ["Code", "Code - Insiders", "Code - OSS", "VSCodium"];

    private readonly string? _home;

    public VsCodeLocalHistoryProvider(string? home = null)
    {
        _home = home;
    }

    public string Name => "vscode";

    public bool SupportsContent => true;

    public string? LastReadError { get; private set; }

    private string Home => _home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public IReadOnlyList<IdeHistoryStoreInfo> Discover()
    {
        var stores = new List<IdeHistoryStoreInfo>();
        foreach (var userDir in CandidateUserDirs())
        {
            var history = Path.Combine(userDir, "History");
            if (!Directory.Exists(history))
                continue;
            stores.Add(new IdeHistoryStoreInfo
            {
                Provider = Name,
                Location = history,
                Retention = DescribeRetention(Path.Combine(userDir, "settings.json")),
            });
        }
        return stores;
    }

    /**
     * 各平台 VS Code 系产品的 User 目录: Windows 在 %APPDATA%, macOS 在 ~/Library/Application Support, Linux 在 ~/.config。
     * 注入 _home 时按 Linux 布局解析(测试缝, 与平台无关)。
     */
    private IEnumerable<string> CandidateUserDirs()
    {
        if (_home is not null)
        {
            foreach (var product in Products)
                yield return Path.Combine(_home, ".config", product, "User");
            yield break;
        }
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var product in Products)
                yield return Path.Combine(appData, product, "User");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(Home, "Library", "Application Support");
            foreach (var product in Products)
                yield return Path.Combine(support, product, "User");
        }
        else
        {
            foreach (var product in Products)
                yield return Path.Combine(Home, ".config", product, "User");
            yield return Path.Combine(Home, ".vscode-server", "data", "User");
        }
    }

    private static string DescribeRetention(string settingsPath)
    {
        const int defaultEntries = 50;
        const int defaultMaxBytes = 256 * 1024;
        var entries = defaultEntries;
        var maxBytes = defaultMaxBytes;
        var enabled = true;
        try
        {
            if (File.Exists(settingsPath)
                && JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject settings)
            {
                entries = IntOf(settings, "workbench.localHistory.maxFileEntries") ?? entries;
                maxBytes = (IntOf(settings, "workbench.localHistory.maxFileSize") ?? maxBytes / 1024) * 1024;
                enabled = BoolOf(settings, "workbench.localHistory.enabled") ?? enabled;
            }
        }
        catch (JsonException)
        {
        }
        return $"workbench.localHistory maxFileEntries={entries}, maxFileSize={maxBytes / 1024}KB, enabled={enabled} (按条数淘汰,无按天过期)";
    }

    private static int? IntOf(JsonObject settings, string key)
        => settings[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static bool? BoolOf(JsonObject settings, string key)
        => settings[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    public IReadOnlyList<IdeHistoryEntry> List(IdeHistoryStoreInfo store, string? pathFilter, int limit)
    {
        var entries = new List<IdeHistoryEntry>();
        foreach (var directory in Directory.EnumerateDirectories(store.Location))
        {
            var entriesFile = Path.Combine(directory, "entries.json");
            if (!File.Exists(entriesFile))
                continue;
            var (resource, items) = ReadEntriesFile(entriesFile);
            if (resource is null)
                continue;
            if (pathFilter is { Length: > 0 } && !resource.Contains(pathFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var (id, timestamp) in items)
            {
                entries.Add(new IdeHistoryEntry
                {
                    Provider = Name,
                    Path = resource,
                    Timestamp = timestamp,
                    Kind = "content",
                    EntryFile = Path.Combine(directory, id),
                });
            }
        }
        return [.. entries.OrderByDescending(entry => entry.Timestamp).Take(limit)];
    }

    public IdeHistoryContent? Read(IdeHistoryStoreInfo store, IdeHistoryEntry entry)
    {
        LastReadError = null;
        if (entry.EntryFile is null || !File.Exists(entry.EntryFile))
        {
            LastReadError = "历史条目文件在磁盘上不存在";
            return null;
        }
        return new IdeHistoryContent
        {
            Bytes = File.ReadAllBytes(entry.EntryFile),
            Origin = $"{Name}:{entry.EntryFile}",
        };
    }

    /** VS Code 把 Windows 盘符冒号百分号编码(file:///c%3A/...),Uri.LocalPath 还原成 /c:/...;补回盘符开头。 */
    private static string LocalPathOf(string resource)
    {
        var path = new Uri(resource).LocalPath;
        return path.Length > 2 && path[0] == '/' && path[2] == ':' ? path[1..] : path;
    }

    private static (string? Resource, List<(string Id, long Timestamp)> Items) ReadEntriesFile(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                return (null, []);
            var resource = root["resource"]?.GetValue<string>();
            if (resource is not { Length: > 0 })
                return (null, []);
            var local = resource.StartsWith("file://", StringComparison.Ordinal)
                ? LocalPathOf(resource)
                : resource;
            var items = new List<(string, long)>();
            if (root["entries"] is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not JsonObject entry)
                        continue;
                    var id = entry["id"]?.GetValue<string>();
                    if (id is not { Length: > 0 })
                        continue;
                    items.Add((id, entry["timestamp"]?.GetValue<long>() ?? 0));
                }
            }
            return (local, items);
        }
        catch (Exception error) when (error is JsonException or UriFormatException or InvalidOperationException)
        {
            return (null, []);
        }
    }
}
