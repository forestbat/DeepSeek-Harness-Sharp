using System.Globalization;
using Dsh.Plugins;
using Dsh.Runtime;
using System.Text.Encodings.Web;
using Dsh.Runtime.Logging;
using YamlDotNet.Core;
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

    [YamlMember(Alias = "tools")]
    public ToolsSettings? Tools { get; set; }

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

    [YamlMember(Alias = "scripts")]
    public ScriptsSettings? Scripts { get; set; }

    [YamlIgnore]
    public Dictionary<string, PluginSetting> Plugins { get; set; } = [];

    private string? _savedDocument;
    private string? _savedPath;
    private static readonly AsyncLocal<string?> PublicationPath = new();
    private static readonly AsyncLocal<string?> PreparationPath = new();

    public static string LoadTemplate(string? baseDirectory = null)
    {
        var templatePath = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "Profiles", "Templates", "settings.yaml");
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("找不到随包发布的 settings.yaml 模板; 安装不完整, 请重新安装或修复安装。", templatePath);
        return File.ReadAllText(templatePath);
    }

    /** 上一次读取修复过文件时的说明(哪几段被丢弃、原文落在哪); 未修复为 null。不属于配置面, 不对外暴露。 */
    [YamlIgnore]
    internal string? Notice { get; private set; }

    public static HarnessSettings Load(HarnessHome home)
    {
        Directory.CreateDirectory(home.Root);
        using var transaction = new SettingsWriteLock(home.SettingsFile);
        return LoadSnapshot(home.SettingsFile, transaction.CanonicalPath);
    }

    public static HarnessSettings LoadForReview(HarnessHome home)
    {
        var canonical = CanonicalFilePath.Canonicalize(home.SettingsFile);
        if (PublicationPath.Value == canonical)
            return LoadSnapshot(home.SettingsFile, canonical);
        using var transaction = new SettingsWriteLock(home.SettingsFile);
        return LoadSnapshot(home.SettingsFile, transaction.CanonicalPath);
    }

    private static HarnessSettings LoadSnapshot(string path, string canonicalPath)
    {
        var text = ReadDocument(path, out var notice);
        var settings = Parse(text);
        settings.Plugins = PluginSettingsSection.ParseDocument(text);
        settings._savedDocument = text;
        settings._savedPath = canonicalPath;
        settings.Notice = notice;
        return settings;
    }

    internal static HarnessSettings Parse(string text)
    {
        ScriptsSettings.ValidateDocument(text);
        return new StaticDeserializerBuilder(new DshYamlStaticContext())
            .WithAttemptingUnquotedStringTypeDeserialization()
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<HarnessSettings>(text);
    }

    /** 启动与写入共用的唯一判据: 类型化 settings 与 plugins 段都要吃得下这份文本。 */
    public static void Validate(string text)
    {
        _ = Parse(text);
        _ = PluginSettingsSection.ParseDocument(text);
    }

    public static bool TryValidate(string text, out string? message)
    {
        try
        {
            Validate(text);
            message = null;
            return true;
        }
        catch (YamlException error)
        {
            message = Describe(error);
            return false;
        }
        catch (Exception error) when (error is RuntimeException or InvalidDataException)
        {
            message = error.Message;
            return false;
        }
    }

    public static string Describe(YamlException error)
        => error.InnerException is { } inner
            ? $"第 {error.Start.Line} 行 {error.Start.Column} 列: {error.Message} ({inner.Message})"
            : $"第 {error.Start.Line} 行 {error.Start.Column} 列: {error.Message}";

    /**
     * settings.yaml 的唯一读入口: 能解析就用; 不能解析就丢掉坏的那条 entry 并写回, 原文隔离保存。
     * 丢弃会放宽限制的段(plugins / sandbox)不修 —— 猜权限比不启动更糟。
     */
    private static string ReadDocument(string path, out string? notice)
    {
        notice = null;
        var text = File.Exists(path) ? ReadSettingsTextShared(path) : LoadTemplate();
        try
        {
            Validate(text);
            return text;
        }
        catch (YamlException error)
        {
            var quarantined = File.Exists(path) ? Quarantine(path, text) : path;
            var attempt = SettingsRepair.Repair(text, error);
            if (attempt.Text is { } repaired)
            {
                WriteAtomic(path, repaired);
                notice = attempt.Describe(quarantined);
                return repaired;
            }
            throw new InvalidDataException(
                $"settings.yaml {Describe(error)}; {attempt.Refusal}; 原文已保存为 {quarantined}; 修好这一段再启动。");
        }
    }

    private static string Quarantine(string path, string text)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        for (var attempt = 0; ; attempt++)
        {
            var candidate = $"{path}{SettingsRepair.QuarantineMarker}{stamp}{(attempt == 0 ? "" : $"-{attempt}")}";
            try
            {
                SettingsFilePermissions.Write(candidate, text);
                return candidate;
            }
            catch (IOException) when (attempt < 60)
            {
                // 同一秒内已有隔离文件: 换个后缀再试。
            }
        }
    }

    /** settings_set 的唯一写入口: 加锁读 → 改一条路径 → 校验 → 原子替换。 */
    public static void SetEntry(HarnessHome home, IReadOnlyList<string> path, object? value)
    {
        Directory.CreateDirectory(home.Root);
        using var transaction = new SettingsWriteLock(home.SettingsFile);
        var existing = ReadDocument(home.SettingsFile, out _);
        var rendered = value is null ? null : SettingsDocument.FlowValue(value);
        var updated = SettingsDocument.ReplacePath(existing, path, rendered);
        if (!TryValidate(updated, out var problem))
            throw new InvalidDataException($"settings_set 的结果无法解析({problem}); 文件未改动。");
        WriteAtomic(home.SettingsFile, updated);
    }

    /** 守卫还原: 只接受已通过校验的文本, 与其它写者共用同一把跨进程锁。 */
    public static void Restore(HarnessHome home, string text)
    {
        Validate(text);
        Directory.CreateDirectory(home.Root);
        using var transaction = new SettingsWriteLock(home.SettingsFile);
        WriteAtomic(home.SettingsFile, text);
    }

    public static string Quarantine(HarnessHome home, string text) => Quarantine(home.SettingsFile, text);

    /** Human updates stay transactional; tool updates release the lock while awaiting a decision. */
    public static bool Update(HarnessHome home, Func<HarnessSettings, bool> update)
        => MutateDocument(home.SettingsFile, (existing, canonical) =>
        {
            var settings = Parse(existing);
            settings.Plugins = PluginSettingsSection.ParseDocument(existing);
            settings._savedDocument = existing;
            settings._savedPath = canonical;
            return update(settings) ? settings.PrepareSnapshot(existing, canonical) : null;
        });

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
        string? saved = null;
        MutateDocument(home.SettingsFile, (existing, canonical) => saved = PrepareSnapshot(existing, canonical));
        if (saved is not null)
        {
            _savedDocument = saved;
            _savedPath = CanonicalFilePath.Canonicalize(home.SettingsFile);
        }
    }

    private string PrepareSnapshot(string existing, string canonicalPath)
    {
        var serializer = new StaticSerializerBuilder(new DshYamlStaticContext())
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .WithQuotingNecessaryStrings()
            .Build();
        var text = serializer.Serialize(this);
        var previous = _savedPath == canonicalPath ? _savedDocument ?? existing : existing;
        var updated = SettingsDocument.MergeTopLevel(existing, serializer.Serialize(Parse(previous)), text);
        Validate(updated);
        return updated;
    }

    public void SavePlugins(HarnessHome home)
        => MutateDocument(home.SettingsFile, (existing, _) => SettingsDocument.MergePlugins(existing, Plugins), backup: true);

    /** Changes one entry from the current locked document without replaying a stale plugin dictionary. */
    public static void UpdatePlugin(HarnessHome home, string package, Func<PluginSetting?, PluginSetting> update)
        => UpdatePlugins(home, [package], update);

    /** Commits a multi-package install proposal against one current document and one approval. */
    public static void UpdatePlugins(HarnessHome home, IReadOnlyList<string> packages, Func<PluginSetting?, PluginSetting> update)
        => MutateDocument(home.SettingsFile, (existing, _) => MergePluginUpdates(existing, packages, update), backup: true);

    public static Task UpdatePluginAsync(HarnessHome home, string package, Func<PluginSetting?, PluginSetting> update, ISettingsMutationContext? run = null)
        => UpdatePluginsAsync(home, [package], update, run);

    public static Task UpdatePluginsAsync(HarnessHome home, IReadOnlyList<string> packages, Func<PluginSetting?, PluginSetting> update, ISettingsMutationContext? run = null)
    {
        if (run is not null)
            return MutateDocumentAsync(home.SettingsFile, (existing, _) => MergePluginUpdates(existing, packages, update), run, backup: true);
        UpdatePlugins(home, packages, update);
        return Task.CompletedTask;
    }

    private static string MergePluginUpdates(string existing, IReadOnlyList<string> packages, Func<PluginSetting?, PluginSetting> update)
    {
        var plugins = PluginSettingsSection.ParseDocument(existing);
        foreach (var package in packages.Distinct(StringComparer.Ordinal))
            plugins[package] = update(plugins.GetValueOrDefault(package));
        return SettingsDocument.MergePlugins(existing, plugins);
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
        MutateDocument(home.SettingsFile, (existing, _) => SettingsDocument.ReplaceMappingChild(existing, "network", "outbound_proxy", serialized));
        proxy.Configure(value.Url, value.Bypass);
    }

    /** Persists only the shared selection; transport validation and publication belong to the route controller. */
    public static void SaveBorrowedOutboundMachine(HarnessHome home, string? machine)
    {
        var serialized = machine is null ? null : $"\"{JavaScriptEncoder.Default.Encode(machine)}\"";
        MutateDocument(home.SettingsFile, (existing, _) => SettingsDocument.ReplaceMappingChild(existing, "network", "borrowed_outbound_machine", serialized));
    }

    /** Applies a raw document transform while owning the settings writer transaction. */
    internal static bool MutateDocument(string path, Func<string, string, string?> prepare, bool backup = false)
    {
        var canonical = CanonicalFilePath.Canonicalize(path);
        using var transaction = new SettingsWriteLock(canonical);
        var before = File.Exists(canonical) ? ReadDocument(canonical, out _) : "";
        if (PrepareMutation(before, canonical, prepare) is not { } after)
            return false;
        Publish(canonical, before, after, backup);
        return true;
    }

    private static Task<bool> MutateDocumentAsync(string path, Func<string, string, string?> prepare, ISettingsMutationContext run, bool backup)
        => run.CommitAsync(path, canonical =>
        {
            using var transaction = new SettingsWriteLock(canonical);
            var before = File.Exists(canonical) ? ReadDocument(canonical, out _) : "";
            return PrepareMutation(before, canonical, prepare) is { } after
                ? new SettingsMutationProposal(before, after)
                : null;
        }, (canonical, proposal, temporary, signal) =>
        {
            if (backup && proposal.Before != proposal.After && proposal.Before.Length > 0)
                WriteAtomic($"{canonical}.bak", proposal.Before);
            return File.WriteAllTextAsync(temporary, proposal.After, signal);
        });

    private static string? PrepareMutation(string before, string canonical, Func<string, string, string?> prepare)
    {
        if (PreparationPath.Value == canonical)
            throw new InvalidOperationException("A settings update callback cannot recursively persist settings.");
        PreparationPath.Value = canonical;
        try
        {
            var after = prepare(before.Length == 0 ? LoadTemplate() : before, canonical);
            if (after is not null)
                Validate(after);
            return after;
        }
        finally
        {
            PreparationPath.Value = null;
        }
    }

    private static void Publish(string canonical, string before, string after, bool backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        if (backup && before != after && before.Length > 0)
            WriteAtomic($"{canonical}.bak", before);
        WriteAtomic(canonical, after);
    }

    /** The mutex remains on its owning thread even when a broker's real commit callback is asynchronous. */
    public static Task CommitApprovedMutationAsync(string path, string before, string after, Func<string, CancellationToken, Task> commit, CancellationToken signal)
        => Task.Run(() =>
        {
            using var transaction = new SettingsWriteLock(path);
            signal.ThrowIfCancellationRequested();
            var current = File.Exists(path) ? ReadSettingsTextShared(path) : "";
            if (current != before)
                throw new InvalidOperationException("Settings changed while approval was pending; read the current settings and propose again.");
            Validate(after);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            PublicationPath.Value = transaction.CanonicalPath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                SettingsFilePermissions.Write(temporary, "");
                commit(temporary, signal).GetAwaiter().GetResult();
                signal.ThrowIfCancellationRequested();
                if (ReadSettingsTextShared(temporary) != after)
                    throw new IOException("The settings writer did not produce the approved document.");
                if (File.Exists(path))
                    File.Replace(temporary, path, destinationBackupFileName: null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                PublicationPath.Value = null;
                File.Delete(temporary);
            }
        }, signal);

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
            var canonical = CanonicalFilePath.Canonicalize(path);
            CanonicalPath = canonical;
            if (PublicationPath.Value == canonical)
                throw new InvalidOperationException("A settings commit callback cannot reenter settings persistence.");
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

    /// 远程技能源: 每个 URL 返回 {"skills":[{"name","description","whenToUse","content"}]}
    [YamlMember(Alias = "urls")]
    public List<string> Urls { get; set; } = [];

    /// 允许上架的远程技能名; 留空表示一个都不要(默认), 远端内容因此进不了提示词。
    [YamlMember(Alias = "enabled")]
    public List<string> Enabled { get; set; } = [];
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

    [YamlMember(Alias = "whitelist")]
    public List<string> Whitelist { get; set; } = [];
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

/** 工具集行为: 渐进式披露与其阈值。 */
public sealed class ToolsSettings
{
    public const int DefaultDisclosureThreshold = 8;

    /**
     * 是否启用渐进式披露: 成组的大工具集默认对模型不可见, 由 tool_router 展开。
     * 模型不一定理解"路由工具"这种约定, 所以默认关闭 —— 所有工具直接可见, 需要省 token 时再打开。
     */
    [YamlMember(Alias = "progressive_disclosure")]
    public bool ProgressiveDisclosure { get; set; }

    /** 启用渐进式披露时, 工具数达到该阈值才整组隐藏; 更小的组直接可见, 不值得多一次路由往返。 */
    [YamlMember(Alias = "disclosure_threshold")]
    public int DisclosureThreshold { get; set; } = DefaultDisclosureThreshold;
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
