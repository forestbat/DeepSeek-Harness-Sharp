using Dsh.Plugins;
using Dsh.Runtime;
using System.Text.Encodings.Web;
using Dsh.Runtime.Logging;
using YamlDotNet.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace Dsh.Boot;

/** 配置文件位置：%USERPROFILE%\.dsh\settings.yaml（即 $DSH_HOME/settings.yaml，默认 ~/.dsh/settings.yaml）。标记为 settings 文档: 生成器据此产出顶层配置面(schema/模板/校验共用)。 */
[DshSettingsDocument]
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

    [YamlMember(Alias = "fleet")]
    public FleetSettings? Fleet { get; set; }

    [YamlMember(Alias = "network")]
    public NetworkSettings? Network { get; set; }

    [YamlMember(Alias = "sandbox")]
    public SandboxSettings? Sandbox { get; set; }

    [YamlIgnore]
    public Dictionary<string, PluginSetting> Plugins { get; set; } = [];

    private string? _savedDocument;
    private string? _savedPath;

    public static string LoadTemplate(string? baseDirectory = null)
    {
        var templatePath = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "Profiles", "Templates", "settings.yaml");
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("找不到随包发布的 settings.yaml 模板; 安装不完整, 请重新安装或修复安装。", templatePath);
        return File.ReadAllText(templatePath);
    }

    public static HarnessSettings Load(HarnessHome home)
    {
        Directory.CreateDirectory(home.Root);
        using var transaction = new SettingsWriteLock(home.SettingsFile);
        return LoadSnapshot(home.SettingsFile, transaction.CanonicalPath);
    }

    private static HarnessSettings LoadSnapshot(string path, string canonicalPath)
    {
        var text = File.Exists(path) ? ReadSettingsTextShared(path) : LoadTemplate();
        var settings = Parse(text);
        settings.Plugins = PluginSettingsSection.ParseDocument(text);
        settings._savedDocument = text;
        settings._savedPath = canonicalPath;
        return settings;
    }

    private static HarnessSettings Parse(string text)
        => new StaticDeserializerBuilder(new DshYamlStaticContext())
            .WithAttemptingUnquotedStringTypeDeserialization()
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<HarnessSettings>(text);

    /** Runs a synchronous mutation under one cross-process transaction; false leaves the document unchanged. */
    public static bool Update(HarnessHome home, Func<HarnessSettings, bool> update)
    {
        using var transaction = new SettingsWriteLock(home.SettingsFile);
        var settings = LoadSnapshot(home.SettingsFile, transaction.CanonicalPath);
        if (!update(settings))
            return false;
        Directory.CreateDirectory(home.Root);
        settings.SaveSnapshot(home.SettingsFile, transaction.CanonicalPath);
        return true;
    }

    /** Initializes a migration destination without racing another settings writer or locking the source. */
    internal static void CopyIfMissing(HarnessHome source, HarnessHome destination)
    {
        using var transaction = new SettingsWriteLock(destination.SettingsFile);
        if (!File.Exists(destination.SettingsFile) && File.Exists(source.SettingsFile))
            WriteAtomic(destination.SettingsFile, ReadSettingsTextShared(source.SettingsFile));
    }

    /** Applies the caller's changed top-level sections without reverting other writers' unchanged sections. */
    public void Save(HarnessHome home)
    {
        Directory.CreateDirectory(home.Root);
        using var transaction = new SettingsWriteLock(home.SettingsFile);
        SaveSnapshot(home.SettingsFile, transaction.CanonicalPath);
    }

    private void SaveSnapshot(string path, string canonicalPath)
    {
        // Strings that read back as another type (a password "null", a key "123") are quoted, or they would not round-trip.
        var serializer = new StaticSerializerBuilder(new DshYamlStaticContext())
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .WithQuotingNecessaryStrings()
            .Build();
        var existing = File.Exists(path) ? ReadSettingsTextShared(path) : _savedPath == canonicalPath ? _savedDocument : null;
        var text = serializer.Serialize(this);
        if (existing is null)
        {
            WriteAtomic(path, text);
            _savedDocument = text;
            _savedPath = canonicalPath;
            return;
        }
        var previous = _savedPath == canonicalPath ? _savedDocument ?? existing : existing;
        var updated = SettingsDocument.MergeTopLevel(existing, serializer.Serialize(Parse(previous)), text);
        _ = Parse(updated);
        WriteAtomic(path, updated);
        _savedDocument = text;
        _savedPath = canonicalPath;
    }

    public void SavePlugins(HarnessHome home)
    {
        var path = Path.Combine(home.Root, "settings.yaml");
        Directory.CreateDirectory(home.Root);
        using var transaction = new SettingsWriteLock(path);
        var text = File.Exists(path) ? ReadSettingsTextShared(path) : LoadTemplate();
        _ = Parse(text);
        var updated = SettingsDocument.ReplacePluginsBlock(text, SettingsDocument.RenderPlugins(Plugins));
        _ = Parse(updated);
        WriteAtomic(path, updated);
    }

    /** Validate independently, persist only the owned child, then publish the route to existing clients. */
    public static void SaveOutboundProxy(HarnessHome home, OutboundProxySettings value, OutboundProxy proxy)
    {
        using var candidate = new OutboundProxy();
        candidate.Configure(value.Url, value.Bypass);
        var empty = value.Url is null && value.Bypass.Count == 0 && value.ProbeUrl is null;
        var serialized = empty ? null : new StaticSerializerBuilder(new DshYamlStaticContext())
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .JsonCompatible()
            .Build()
            .Serialize(value).TrimEnd();
        var path = home.SettingsFile;
        Directory.CreateDirectory(home.Root);
        using var transaction = new SettingsWriteLock(path);
        var existing = File.Exists(path) ? ReadSettingsTextShared(path) : LoadTemplate();
        _ = Parse(existing);
        var updated = SettingsDocument.ReplaceMappingChild(existing, "network", "outbound_proxy", serialized);
        _ = Parse(updated);
        WriteAtomic(path, updated);
        proxy.Configure(value.Url, value.Bypass);
    }

    /** Persists only the shared selection; transport validation and publication belong to the route controller. */
    public static void SaveBorrowedOutboundMachine(HarnessHome home, string? machine)
    {
        var serialized = machine is null ? null : $"\"{JavaScriptEncoder.Default.Encode(machine)}\"";
        var path = home.SettingsFile;
        Directory.CreateDirectory(home.Root);
        using var transaction = new SettingsWriteLock(path);
        var existing = File.Exists(path) ? ReadSettingsTextShared(path) : LoadTemplate();
        _ = Parse(existing);
        var updated = SettingsDocument.ReplaceMappingChild(existing, "network", "borrowed_outbound_machine", serialized);
        _ = Parse(updated);
        WriteAtomic(path, updated);
    }

    /** Keep a complete snapshot while allowing WriteAtomic to replace settings.yaml on Windows. */
    private static string ReadSettingsTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /** 先写同目录临时文件再改名,避免进程中断留下半截配置; settings.yaml 一律收紧为仅属主可读写。 */
    private static void WriteAtomic(string path, string content)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            SettingsFilePermissions.Write(temporary, content);
            if (File.Exists(path))
                File.Replace(temporary, path, destinationBackupFileName: null);
            else
                File.Move(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /** The mutex covers the document, not just its rename; unrelated homes never share a lock. */
    private sealed class SettingsWriteLock : IDisposable
    {
        private readonly Mutex _mutex;

        public string CanonicalPath { get; }

        public SettingsWriteLock(string path)
        {
            var canonical = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
                canonical = canonical.ToUpperInvariant();
            CanonicalPath = canonical;
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            _mutex = new Mutex(false, $"Dsh.Settings.{digest}", new NamedWaitHandleOptions
            {
                CurrentUserOnly = true,
                CurrentSessionOnly = false,
            });
            try
            {
                _mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // The prior writer exited; atomic publication left either the old or the new document.
            }
            catch
            {
                _mutex.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    public (string Provider, string Model)? ResolveDefaultModel() => ParseModelRoute(GlobalDefaultModel);

    /** Split a `provider/model` setting at the first slash; null when either part is missing. */
    public static (string Provider, string Model)? ParseModelRoute(string? value)
    {
        if (value is not { Length: > 0 })
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

/** Plugin sandbox switch; per-plugin grants live under plugins."<package>".permissions. */
public sealed class SandboxSettings
{
    /** Plugin sandbox switch; false (the default) keeps plugin loading and execution unchanged. */
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; }
}

public sealed class StorageSettings
{
    /** 统一持久化根; 留空则用 $DSH_HOME 或 ~/.dsh。改动后下次启动后台迁移旧数据。 */
    [YamlMember(Alias = "root")]
    public string? Root { get; set; }

    [YamlMember(Alias = "tool_results")]
    public ToolResultsSettings? ToolResults { get; set; }

    /** 各分区目录(相对根 / 绝对路径 / ~ 展开); 留空用同名默认目录。见 DshPaths。 */
    [YamlMember(Alias = "sessions")]
    public string? Sessions { get; set; }

    [YamlMember(Alias = "logs")]
    public string? Logs { get; set; }

    [YamlMember(Alias = "cache")]
    public string? Cache { get; set; }

    [YamlMember(Alias = "checkpoints")]
    public string? Checkpoints { get; set; }

    [YamlMember(Alias = "attachments")]
    public string? Attachments { get; set; }

    [YamlMember(Alias = "telemetry")]
    public string? Telemetry { get; set; }

    [YamlMember(Alias = "run")]
    public string? Run { get; set; }
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
