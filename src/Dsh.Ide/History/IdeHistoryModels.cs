namespace Dsh.Ide.History;

public sealed record IdeHistoryStoreInfo
{
    public required string Provider { get; init; }
    public required string Location { get; init; }
    public required string Retention { get; init; }
}

public interface IIdeHistoryProvider
{
    string Name { get; }

    bool SupportsContent { get; }

    string? LastReadError { get; }

    IReadOnlyList<IdeHistoryStoreInfo> Discover();

    IReadOnlyList<IdeHistoryEntry> List(IdeHistoryStoreInfo store, string? pathFilter, int limit);

    IdeHistoryContent? Read(IdeHistoryStoreInfo store, IdeHistoryEntry entry);
}
