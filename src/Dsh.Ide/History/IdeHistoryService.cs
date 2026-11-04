using System.Security.Cryptography;
using Dsh.Runtime;
using Dsh.Core;
using Dsh.Boot;

namespace Dsh.Ide.History;

public sealed record IdeHistoryOptions
{
    public const int DefaultTimelineLimit = 50;
    public const int DefaultWindowSeconds = 60;
    public IReadOnlyList<string> Providers { get; init; } = [];
    public IReadOnlyDictionary<string, IdeHistoryDiscovery> Discovery { get; init; } = new Dictionary<string, IdeHistoryDiscovery>();
    public bool Timeline { get; init; }
    [global::Dsh.Plugins.DshPluginSettings(Name = "timeline_limit")]
    public int TimelineLimit { get; init; } = DefaultTimelineLimit;
    [global::Dsh.Plugins.DshPluginSettings(Name = "window_seconds")]
    public int WindowSeconds { get; init; } = DefaultWindowSeconds;

    public static IdeHistoryOptions From(object? config)
        => From(config, PluginSettingsSection.DefaultParameter(Plugin.Ide, "history"));

    internal static IdeHistoryOptions From(object? config, object? defaultHistory)
    {
        var map = ConfigMap.Read(config);
        var nested = map.TryGetValue("history", out var history) ? ConfigMap.Read(history) : null;
        var defaults = defaultHistory as IReadOnlyDictionary<string, object?> ?? ConfigMap.Read(defaultHistory);
        object? Value(string key)
            => nested is not null && nested.TryGetValue(key, out var value) ? value : defaults.GetValueOrDefault(key);
        return new IdeHistoryOptions
        {
            Providers = ReadProviders(Value("providers")),
            Discovery = ConfigMap.Read(Value("discovery")).ToDictionary(pair => pair.Key, pair => IdeHistoryDiscovery.From(pair.Value), StringComparer.Ordinal),
            Timeline = Value("timeline") is true,
            TimelineLimit = ReadNumber(Value("timeline_limit"), DefaultTimelineLimit, 1, 500),
            WindowSeconds = ReadNumber(Value("window_seconds"), DefaultWindowSeconds, 0, 3600),
        };
    }

    private static IReadOnlyList<string> ReadProviders(object? value) => value switch
    {
        IEnumerable<object?> list => [.. list.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
        string single => [single],
        _ => [],
    };

    private static int ReadNumber(object? value, int fallback, int minimum, int maximum)
        => (int)Math.Clamp(value switch { int number => number, long number => number, double number when double.IsFinite(number) => number, _ => fallback }, minimum, maximum);
}

public sealed class IdeHistoryService(Context ctx, IdeHistoryRegistry registry, IdeHistoryOptions options)
    : Service(ctx, IIdeHistoryService.ServiceName), IIdeHistoryService, IIdeHistoryTimeline, IIdeHistoryRestore
{
    private const int CandidateMultiplier = 4;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly IEqualityComparer<(string Path, string Digest)> TimelineIdentityComparer =
        EqualityComparer<(string Path, string Digest)>.Create(
            static (left, right) => PathComparer.Equals(left.Path, right.Path) && left.Digest == right.Digest,
            static entry => HashCode.Combine(PathComparer.GetHashCode(entry.Path), StringComparer.Ordinal.GetHashCode(entry.Digest)));

    public bool TimelineEnabled => options.Timeline;
    public IdeWorkspaceKind WorkspaceKind(string? workspaceRoot)
        => WorkspaceIdeDetector.Detect(workspaceRoot, Ctx.Get<IEditorCatalog>(IEditorCatalog.ServiceName, false)?.Descriptors ?? []);

    public IReadOnlyList<IdeHistoryEntry> Query(string? workspaceRoot, long? from, long? to, string? path = null)
    {
        var kind = WorkspaceKind(workspaceRoot);
        if (string.IsNullOrWhiteSpace(workspaceRoot) || kind == IdeWorkspaceKind.None)
            return [];
        return [.. registry.Query(path ?? workspaceRoot, null, options.TimelineLimit * CandidateMultiplier,
                provider => IsEnabled(kind, provider), from, to)
            .Where(item => HistoryRestore.IsWithinWorkspace(workspaceRoot, item.Entry.Path))
            .OrderByDescending(item => item.Entry.Timestamp)
            .ThenBy(item => ProviderOrder(item.Provider.Name))
            .Select(item => item.Entry)
            .Take(options.TimelineLimit)];
    }

    public IdeHistoryContent? Read(IdeHistoryEntry entry) => registry.Read(entry);

    /** Content decoding, deduplication and HEAD checks occur only when a timeline is requested. */
    public IReadOnlyList<IdeHistoryMoment> QueryTimeline(string workspaceRoot, long? from = null, long? to = null)
    {
        var entries = Query(workspaceRoot, from, to);
        var head = GitHeadSnapshot.Read(workspaceRoot, entries.Select(entry => entry.Path).Distinct(PathComparer).ToArray());
        var seen = new HashSet<(string Path, string Digest)>(TimelineIdentityComparer);
        var unique = new List<IdeHistoryEntry>();
        foreach (var entry in entries.OrderBy(entry => ProviderOrder(entry.Provider)).ThenByDescending(entry => entry.Timestamp))
        {
            var content = Read(entry);
            // Search filters are permissive; timeline identity must preserve native filenames.
            var path = Path.GetFullPath(entry.Path);
            var digest = content is null ? $"unavailable:{entry.Provider}:{entry.Timestamp}:{entry.ContentId}" : Convert.ToHexString(SHA256.HashData(content.Bytes));
            if (seen.Add((path, digest)))
                unique.Add(entry with { MatchesGitHead = content is null ? null : head.Matches(entry.Path, content.Bytes) });
        }
        var moments = new List<IdeHistoryMoment>();
        foreach (var entry in unique.OrderByDescending(entry => entry.Timestamp).ThenBy(entry => ProviderOrder(entry.Provider)))
        {
            if (moments.Count == 0 || moments[^1].Timestamp - entry.Timestamp > options.WindowSeconds * 1000L)
                moments.Add(new IdeHistoryMoment(entry.Timestamp, [entry]));
            else
                moments[^1] = moments[^1] with { Entries = [.. moments[^1].Entries, entry] };
        }
        return moments;
    }

    public IdeRestorePreview PreviewRestore(string workspaceRoot, IdeHistoryEntry entry)
        => HistoryRestore.Preview(workspaceRoot, entry, Read(entry)
            ?? throw new InvalidOperationException("History revision content is unavailable."));

    public IdeRestoreResult Restore(IdeRestorePreview preview, bool confirmModified)
    {
        var paths = DshPaths.FromContext(Ctx) ?? throw new InvalidOperationException("DSH storage is not configured.");
        return HistoryRestore.Restore(preview, Path.Combine(paths.CheckpointsPath, "ide-backups"), confirmModified);
    }

    private int ProviderOrder(string name)
    {
        for (var index = 0; index < options.Providers.Count; index++)
            if (name.Contains(options.Providers[index], StringComparison.OrdinalIgnoreCase))
                return index;
        return int.MaxValue;
    }

    private bool IsEnabled(IdeWorkspaceKind kind, string providerName)
        => options.Providers.Any(name => Enum.TryParse<IdeWorkspaceKind>(name, true, out var provider)
            && provider != IdeWorkspaceKind.None && kind.HasFlag(provider)
            && providerName.Contains(name, StringComparison.OrdinalIgnoreCase));
}
