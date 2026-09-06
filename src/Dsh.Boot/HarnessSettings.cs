using YamlDotNet.Serialization;

namespace Dsh.Boot;

/** 配置文件位置：%USERPROFILE%\.dsh\settings.yaml（即 $DSH_HOME/settings.yaml，默认 ~/.dsh/settings.yaml）*/
public sealed class HarnessSettings
{
    [YamlMember(Alias = "provider")]
    public string? Provider { get; init; }

    [YamlMember(Alias = "model")]
    public string? Model { get; init; }

    [YamlMember(Alias = "baseUrl")]
    public string? BaseUrl { get; init; }

    [YamlMember(Alias = "apiKey")]
    public string? ApiKey { get; init; }

    [YamlMember(Alias = "reasoningEffort")]
    public string? ReasoningEffort { get; init; }

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
}