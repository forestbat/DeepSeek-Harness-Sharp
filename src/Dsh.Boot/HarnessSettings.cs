using YamlDotNet.Serialization;

namespace Dsh.Boot;

/** 配置文件位置：%USERPROFILE%\.dsh\settings.yaml（即 $DSH_HOME/settings.yaml，默认 ~/.dsh/settings.yaml） */
public sealed class HarnessSettings
{
    [YamlMember(Alias = "default")]
    public string? Default { get; init; }

    [YamlMember(Alias = "providers")]
    public Dictionary<string, ProviderSettings> Providers { get; init; } = [];

    [YamlMember(Alias = "configs")]
    public Dictionary<string, ConfigSettings> Configs { get; init; } = [];

    [YamlMember(Alias = "safety")]
    public SafetySettings? Safety { get; init; }

    [YamlMember(Alias = "mcpServers")]
    public Dictionary<string, McpServerSettings> McpServers { get; init; } = [];

    public static HarnessSettings Load(HarnessHome home)
    {
        var path = Path.Combine(home.Root, "settings.yaml");
        if (!File.Exists(path))
            return new HarnessSettings();
        var deserializer = new DeserializerBuilder()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build();
        return deserializer.Deserialize<HarnessSettings>(File.ReadAllText(path)) ?? new HarnessSettings();
    }

    public void Save(HarnessHome home)
    {
        var path = Path.Combine(home.Root, "settings.yaml");
        Directory.CreateDirectory(home.Root);
        var serializer = new SerializerBuilder().Build();
        File.WriteAllText(path, serializer.Serialize(this));
    }

    public ConfigSettings? ResolveConfig(string? name = null)
    {
        var configName = name ?? Default;
        if (configName is not null && Configs.TryGetValue(configName, out var selected))
            return selected;
        return Configs.Count == 0 ? null : Configs.Values.First();
    }

    public ProviderSettings? ResolveProvider(string providerName)
        => Providers.GetValueOrDefault(providerName);
}

public sealed class ProviderSettings
{
    [YamlMember(Alias = "type")]
    public string? Type { get; init; }

    [YamlMember(Alias = "baseUrl")]
    public string? BaseUrl { get; init; }

    [YamlMember(Alias = "apiKey")]
    public string? ApiKey { get; init; }

    [YamlMember(Alias = "apiKeyEnv")]
    public string? ApiKeyEnv { get; init; }

    [YamlMember(Alias = "modelIds")]
    public List<string>? ModelIds { get; init; }
}

public sealed class ConfigSettings
{
    [YamlMember(Alias = "provider")]
    public string? Provider { get; init; }

    [YamlMember(Alias = "model")]
    public string? Model { get; init; }

    [YamlMember(Alias = "reasoningEffort")]
    public string? ReasoningEffort { get; init; }
}

public sealed class SafetySettings
{
    [YamlMember(Alias = "autoApprove")]
    public bool AutoApprove { get; init; }

    [YamlMember(Alias = "blacklist")]
    public List<string> Blacklist { get; init; } = [];
}

public sealed class McpServerSettings
{
    [YamlMember(Alias = "transport")]
    public string? Transport { get; init; }

    [YamlMember(Alias = "command")]
    public string? Command { get; init; }

    [YamlMember(Alias = "args")]
    public List<string>? Args { get; init; }

    [YamlMember(Alias = "url")]
    public string? Url { get; init; }
}
