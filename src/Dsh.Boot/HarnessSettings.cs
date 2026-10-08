using Dsh.Runtime.Logging;
using YamlDotNet.Serialization;

namespace Dsh.Boot;

/** 配置文件位置：%USERPROFILE%\.dsh\settings.yaml（即 $DSH_HOME/settings.yaml，默认 ~/.dsh/settings.yaml） */
public sealed class HarnessSettings
{
    [YamlMember(Alias = "global_default_model")]
    public string? GlobalDefaultModel { get; set; }

    [YamlMember(Alias = "compaction_model")]
    public string? CompactionModel { get; set; }

    [YamlMember(Alias = "subagent")]
    public SubagentSettings? Subagent { get; set; }

    [YamlMember(Alias = "providers")]
    public Dictionary<string, ProviderSettings> Providers { get; set; } = [];

    [YamlMember(Alias = "skills")]
    public SkillsSettings? Skills { get; set; }

    [YamlMember(Alias = "rules")]
    public List<string> Rules { get; set; } = [];

    [YamlMember(Alias = "mcp")]
    public Dictionary<string, McpServerSettings> McpServers { get; set; } = [];

    [YamlMember(Alias = "a2a")]
    public A2aSettings? A2a { get; set; }

    [YamlMember(Alias = "compaction")]
    public CompactionSettings? Compaction { get; set; }

    [YamlMember(Alias = "safety")]
    public SafetySettings? Safety { get; set; }

    [YamlMember(Alias = "logging")]
    public LoggingSettings? Logging { get; set; }

    [YamlMember(Alias = "storage")]
    public StorageSettings? Storage { get; set; }

    [YamlIgnore]
    public Dictionary<string, PluginSetting> Plugins { get; set; } = [];

    private const string MinimalSettingsTemplate = """
        global_default_model: deepseek-official/deepseek-v4-flash
        compaction_model: deepseek-official/deepseek-v4-flash
        plugins:
          "@deepseek-ai/dsh-memory": false
          "@deepseek-ai/dsh-checkpoints": false
          "@deepseek-ai/dsh-toon": false
        """;

    public static string LoadTemplate(string? baseDirectory = null)
    {
        var templatePath = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "Profiles", "Templates", "settings.yaml");
        return File.Exists(templatePath) ? File.ReadAllText(templatePath) : MinimalSettingsTemplate;
    }

    public static HarnessSettings Load(HarnessHome home)
    {
        var path = Path.Combine(home.Root, "settings.yaml");
        Directory.CreateDirectory(home.Root);
        if (!File.Exists(path))
            File.WriteAllText(path, LoadTemplate());
        var text = File.ReadAllText(path);
        var deserializer = new StaticDeserializerBuilder(new DshYamlStaticContext())
            .WithAttemptingUnquotedStringTypeDeserialization()
            .IgnoreUnmatchedProperties()
            .Build();
        var settings = deserializer.Deserialize<HarnessSettings>(text);
        settings.Plugins = PluginSettingsSection.ParseDocument(text);
        return settings;
    }

    public void Save(HarnessHome home)
    {
        var path = Path.Combine(home.Root, "settings.yaml");
        Directory.CreateDirectory(home.Root);
        var serializer = new StaticSerializerBuilder(new DshYamlStaticContext()).Build();
        var text = serializer.Serialize(this);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var plugins = existing is null ? null : SettingsDocument.ExtractPluginsBlock(existing);
        WriteAtomic(path, SettingsDocument.ReplacePluginsBlock(text, plugins));
    }

    public void SavePlugins(HarnessHome home)
    {
        var path = Path.Combine(home.Root, "settings.yaml");
        Directory.CreateDirectory(home.Root);
        if (!File.Exists(path))
            File.WriteAllText(path, LoadTemplate());
        var text = File.ReadAllText(path);
        WriteAtomic(path, SettingsDocument.ReplacePluginsBlock(text, SettingsDocument.RenderPlugins(Plugins)));
    }

    /** 先写同目录临时文件再改名,避免进程中断留下半截配置。 */
    private static void WriteAtomic(string path, string content)
    {
        var temporary = $"{path}.tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    public (string Provider, string Model)? ResolveDefaultModel()
    {
        if (GlobalDefaultModel is not { Length: > 0 } value)
            return null;
        var parts = value.Split('/', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            return null;
        return (parts[0], parts[1]);
    }

    public ProviderSettings? ResolveProvider(string providerName)
        => Providers.GetValueOrDefault(providerName);
}

public sealed class ProviderSettings
{
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    [YamlMember(Alias = "options")]
    public ProviderOptions? Options { get; set; }

    [YamlMember(Alias = "models")]
    public Dictionary<string, ProviderModelSettings> Models { get; set; } = [];
}

public sealed class ProviderOptions
{
    [YamlMember(Alias = "baseUrl")]
    public string? BaseUrl { get; set; }

    [YamlMember(Alias = "apiKey")]
    public string? ApiKey { get; set; }

    [YamlMember(Alias = "apiKeyEnv")]
    public string? ApiKeyEnv { get; set; }

    /** 已废弃: OpenAI 族风格改写进 `type`(如 openai-compatible(response)); 仅为兼容读取旧 settings 保留。 */
    [YamlMember(Alias = "apiStyle")]
    public string? ApiStyle { get; set; }
}

public sealed class ProviderModelSettings
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "tool_call")]
    public bool? ToolCall { get; set; }

    [YamlMember(Alias = "system_prompt_update")]
    public string? SystemPromptUpdate { get; set; }
}

public sealed class SubagentSettings
{
    [YamlMember(Alias = "default_model")]
    public string? DefaultModel { get; set; }
}

public sealed class SkillsSettings
{
    [YamlMember(Alias = "paths")]
    public List<string> Paths { get; set; } = [];

    [YamlMember(Alias = "urls")]
    public List<string> Urls { get; set; } = [];
}

public sealed class CompactionSettings
{
    [YamlMember(Alias = "auto")]
    public bool Auto { get; set; } = true;

    [YamlMember(Alias = "prune")]
    public bool Prune { get; set; } = true;
}

public sealed class SafetySettings
{
    [YamlMember(Alias = "autoApprove")]
    public bool AutoApprove { get; set; }

    [YamlMember(Alias = "blacklist")]
    public List<string> Blacklist { get; set; } = [];
}

public sealed class StorageSettings
{
    /** 统一持久化根; 留空则用 $DSH_HOME 或 ~/.dsh。改动后下次启动后台迁移旧数据。 */
    [YamlMember(Alias = "root")]
    public string? Root { get; set; }

    [YamlMember(Alias = "tool_results")]
    public ToolResultsSettings? ToolResults { get; set; }
}

public sealed class ToolResultsSettings
{
    public const long DefaultMaxBytes = 64 * 1024;
    public const int DefaultRetainDays = 30;

    /** 超过该字节数的工具结果落盘, 会话里只留头部/尾部与路径。 */
    [YamlMember(Alias = "max_bytes")]
    public long MaxBytes { get; set; } = DefaultMaxBytes;

    /** 落盘结果的保留天数。 */
    [YamlMember(Alias = "retain_days")]
    public int RetainDays { get; set; } = DefaultRetainDays;
}

public sealed class LoggingSettings
{
    public const int BytesPerMegabyte = 1024 * 1024;

    [YamlMember(Alias = "level")]
    public string? Level { get; set; }

    [YamlMember(Alias = "buffer_size")]
    public int BufferSize { get; set; } = LoggingOptions.DefaultBufferSize;

    [YamlMember(Alias = "console")]
    public bool Console { get; set; }

    [YamlMember(Alias = "file")]
    public bool File { get; set; } = true;

    [YamlMember(Alias = "file_max_mb")]
    public int FileMaxMb { get; set; } = LoggingOptions.DefaultFileMaxBytes / BytesPerMegabyte;

    [YamlMember(Alias = "keep_days")]
    public int KeepDays { get; set; } = LoggingOptions.DefaultKeepDays;

    public LoggingOptions ToOptions() => new()
    {
        Level = Level,
        BufferSize = BufferSize,
        Console = Console,
        File = File,
        FileMaxBytes = FileMaxMb * BytesPerMegabyte,
        KeepDays = KeepDays,
    };
}

public sealed class A2aSettings
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; }

    [YamlMember(Alias = "host")]
    public string? Host { get; set; }

    [YamlMember(Alias = "port")]
    public int Port { get; set; }

    [YamlMember(Alias = "publicUrl")]
    public string? PublicUrl { get; set; }

    [YamlMember(Alias = "authToken")]
    public string? AuthToken { get; set; }

    [YamlMember(Alias = "skill_id")]
    public string? SkillId { get; set; }

    [YamlMember(Alias = "skill_name")]
    public string? SkillName { get; set; }

    [YamlMember(Alias = "skill_description")]
    public string? SkillDescription { get; set; }

    [YamlMember(Alias = "skill_tags")]
    public List<string> SkillTags { get; set; } = [];

    [YamlMember(Alias = "remotes")]
    public Dictionary<string, A2aRemoteSettings> Remotes { get; set; } = [];
}

/** 远端 A2A agent: url 为基地址(用于解析 agent card); token 是简写的 Bearer(无需自写 authorization 头); headers 为其它附加头(如 qwen 的 x-qwen-*)。 */
public sealed class A2aRemoteSettings
{
    [YamlMember(Alias = "url")]
    public string? Url { get; set; }

    [YamlMember(Alias = "token")]
    public string? Token { get; set; }

    [YamlMember(Alias = "headers")]
    public Dictionary<string, string> Headers { get; set; } = [];
}

public sealed class McpServerSettings
{
    [YamlMember(Alias = "transport")]
    public string? Transport { get; set; }

    [YamlMember(Alias = "command")]
    public List<string>? Command { get; set; }

    [YamlMember(Alias = "args")]
    public List<string>? Args { get; set; }

    [YamlMember(Alias = "url")]
    public string? Url { get; set; }

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;
}
