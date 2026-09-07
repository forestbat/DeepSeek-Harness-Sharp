using YamlDotNet.Serialization;

namespace Dsh.Boot.Profiles;

public sealed class ProfileManifest
{
    [YamlMember(Alias = "name")]
    public string? Name { get; init; }

    [YamlMember(Alias = "private")]
    public bool Private { get; init; }

    [YamlMember(Alias = "dependencies")]
    public Dictionary<string, string> Dependencies { get; init; } = [];

    [YamlMember(Alias = "dsh")]
    public ProfileDshSettings Dsh { get; init; } = new();
}

public sealed class ProfileDshSettings
{
    [YamlMember(Alias = "profile")]
    public ProfileDshProfileSettings Profile { get; init; } = new();
}

public sealed class ProfileDshProfileSettings
{
    [YamlMember(Alias = "bundles")]
    public List<string> Bundles { get; init; } = [];

    [YamlMember(Alias = "patchReload")]
    public bool PatchReload { get; init; }
}

public sealed record ProfileLayer(string PackageName, string PackageDir, string PatchPath, IReadOnlyList<string> Patches);

public sealed class Profile
{
    public required string Name { get; init; }
    public required string Dir { get; init; }
    public IReadOnlyList<ProfileLayer> Layers { get; init; } = [];
    public string? PatchPath { get; init; }
    public IReadOnlyList<string> Patches { get; init; } = [];
    public bool PatchReload { get; init; }
}

public static class ProfileTemplates
{
    public static readonly IReadOnlyList<string> Names = ["headless", "tui", "web", "sdk", "acp", "lsp", "sdk-minimal"];
}