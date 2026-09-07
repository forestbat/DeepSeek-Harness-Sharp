using System.Text.Json;

namespace Dsh.Boot.Profiles;

public static class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string ResolveProfileDir(HarnessHome home, string name)
        => Path.Combine(home.ProfilesPath, name);

    public static ProfileManifest ReadManifest(string directory)
    {
        var path = Path.Combine(directory, "package.json");
        if (!File.Exists(path))
            return new ProfileManifest { Name = Path.GetFileName(directory) };
        return JsonSerializer.Deserialize<ProfileManifest>(File.ReadAllText(path), JsonOptions) ?? new ProfileManifest();
    }

    public static void WriteManifest(string directory, ProfileManifest manifest)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package.json"), JsonSerializer.Serialize(manifest, JsonOptions));
    }

    public static void InitProfile(HarnessHome home, string name)
    {
        var directory = ResolveProfileDir(home, name);
        Directory.CreateDirectory(directory);
        var manifest = name == "sdk-minimal"
            ? new ProfileManifest
            {
                Name = name,
                Dsh = new ProfileDshSettings
                {
                    Profile = new ProfileDshProfileSettings
                    {
                        Bundles = ["@deepseek-ai/dsh-sdk-minimal"],
                    },
                },
            }
            : new ProfileManifest { Name = name };
        WriteManifest(directory, manifest);
        var patchPath = Path.Combine(directory, "cordis.patch.yml");
        if (!File.Exists(patchPath))
            File.WriteAllText(patchPath, "[]\n");
        var workspacePath = Path.Combine(directory, "pnpm-workspace.yaml");
        if (!File.Exists(workspacePath))
            File.WriteAllText(workspacePath, "packages:\n  - .\n");
    }
}