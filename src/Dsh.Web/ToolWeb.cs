using Cordis;

namespace Dsh.Web;

public sealed class ToolWebConfig
{
    public bool Search { get; init; } = true;
    public bool Fetch { get; init; } = true;
    public int SearchMaxResults { get; init; } = WebSearchTool.DefaultMaxResults;
    public int SearchMaxQueries { get; init; } = WebSearchTool.DefaultMaxQueries;
    public long FetchTimeoutMs { get; init; } = WebFetchTool.DefaultTimeoutMs;
    public long SearchTimeoutMs { get; init; } = WebFetchTool.DefaultTimeoutMs;
    public int FetchMaxOutputChars { get; init; } = WebFetchTool.DefaultMaxOutputChars;
}

public static class ToolWeb
{
    public static IDisposable Apply(Context ctx, object? config = null)
    {
        var resolved = config switch
        {
            ToolWebConfig typed => typed,
            null => new ToolWebConfig(),
            IReadOnlyDictionary<string, object?> dict => FromDictionary(dict),
            _ => throw new ArgumentException("tool-web config must be an object or ToolWebConfig", nameof(config)),
        };
        AssertPositiveInteger("searchMaxResults", resolved.SearchMaxResults);
        AssertPositiveInteger("searchMaxQueries", resolved.SearchMaxQueries);
        AssertPositiveInteger("fetchTimeoutMs", resolved.FetchTimeoutMs);
        AssertPositiveInteger("searchTimeoutMs", resolved.SearchTimeoutMs);
        AssertPositiveInteger("fetchMaxOutputChars", resolved.FetchMaxOutputChars);
        var registrations = new List<IDisposable>();
        if (resolved.Search)
            registrations.Add(WebSearchTool.Register(ctx, resolved.SearchMaxResults, resolved.SearchMaxQueries, resolved.SearchTimeoutMs, resolved.Fetch));
        if (resolved.Fetch)
            registrations.Add(WebFetchTool.Register(ctx, resolved.FetchTimeoutMs, resolved.FetchMaxOutputChars));
        return new WebDisposable([.. registrations]);
    }

    private static ToolWebConfig FromDictionary(IReadOnlyDictionary<string, object?> dict)
    {
        return new ToolWebConfig
        {
            Search = dict.GetValueOrDefault("search") is not false,
            Fetch = dict.GetValueOrDefault("fetch") is not false,
            SearchMaxResults = IntOf(dict, "searchMaxResults") ?? WebSearchTool.DefaultMaxResults,
            SearchMaxQueries = IntOf(dict, "searchMaxQueries") ?? WebSearchTool.DefaultMaxQueries,
            FetchTimeoutMs = LongOf(dict, "fetchTimeoutMs") ?? WebFetchTool.DefaultTimeoutMs,
            SearchTimeoutMs = LongOf(dict, "searchTimeoutMs") ?? WebFetchTool.DefaultTimeoutMs,
            FetchMaxOutputChars = IntOf(dict, "fetchMaxOutputChars") ?? WebFetchTool.DefaultMaxOutputChars,
        };
    }

    private static int? IntOf(IReadOnlyDictionary<string, object?> dict, string key)
        => dict.GetValueOrDefault(key) switch
        {
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            int value => value,
            double value when double.IsFinite(value) && value % 1 == 0 => (int)value,
            _ => null,
        };

    private static long? LongOf(IReadOnlyDictionary<string, object?> dict, string key)
        => dict.GetValueOrDefault(key) switch
        {
            long value => value,
            int value => value,
            double value when double.IsFinite(value) && value % 1 == 0 => (long)value,
            _ => null,
        };

    private static void AssertPositiveInteger(string name, long value)
    {
        if (value < 1)
            throw new ArgumentException($"tool-web: {name} must be a positive integer");
    }
}
