namespace Dsh.Boot;

public interface ICredentials
{
    string? Get(string reference);
}

public sealed class EnvCredentials : ICredentials
{
    private readonly IReadOnlyDictionary<string, string> _layered;

    public EnvCredentials(HarnessHome home, string? projectDirectory = null)
    {
        _layered = LoadLayeredEnv(home, projectDirectory);
    }

    public string? Get(string reference)
    {
        if (_layered.TryGetValue(reference, out var layered))
            return layered;
        return Environment.GetEnvironmentVariable(reference);
    }

    private static IReadOnlyDictionary<string, string> LoadLayeredEnv(HarnessHome home, string? projectDirectory)
    {
        var layered = new Dictionary<string, string>();
        foreach (var file in new[]
        {
            Path.Combine(home.Root, ".env"),
            projectDirectory is null ? null : Path.Combine(projectDirectory, ".env"),
        })
        {
            if (file is null || !File.Exists(file))
                continue;
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;
                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                    continue;
                var key = trimmed[..separator].Trim();
                var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
                layered.TryAdd(key, value);
            }
        }
        return layered;
    }
}
