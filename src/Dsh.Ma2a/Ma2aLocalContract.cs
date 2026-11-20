using System.Text.Json;

namespace Dsh.Ma2a;

/** Shared native command fields, projected from MA2A's pinned local API v1 schema. */
public static class Ma2aLocalContract
{
    public static JsonElement Contract { get; } = Load();

    private static JsonElement Load()
    {
        using var stream = typeof(Ma2aLocalContract).Assembly.GetManifestResourceStream("Dsh.Ma2a.LocalApiCatalog.json")
            ?? throw new InvalidOperationException("The native MA2A command contract is missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
