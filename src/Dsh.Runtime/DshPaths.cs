namespace Dsh.Runtime;

/**
 * §18 统一持久化路径: 唯一根 + 可配置分区(storage.sessions/logs/…)。
 * 分区取值可以是相对根的目录名(缺省=分区名)、绝对路径、或以 `~` 开头的用户目录。
 * 所有模块都从这里取路径, 不再各自 `Path.Combine(home, "…")`; 外部 OS 集成(桌面项、plugins 目录、
 * 项目内 .dsh-memory)与 IDE 自身的用户级目录不走这里。
 * 放在 Dsh.Runtime(而非 Dsh.Boot)是为了让 Dsh.Core 等下层模块也能用同一契约。
 */
public sealed record DshPaths(string Root, IReadOnlyDictionary<string, string> Partitions)
{
    /** ctx 属性名: 启动时由 Dsh.Boot 写入, 模块按此取用。 */
    public const string PropName = "dshPaths";

    public const string Sessions = "sessions";
    public const string Logs = "logs";
    public const string Cache = "cache";
    public const string Checkpoints = "checkpoints";
    public const string Attachments = "attachments";
    public const string ToolResults = "tool-results";
    public const string Telemetry = "telemetry";
    public const string Run = "run";

    /** 命令输出溢出的原文目录: 跟随存储根派生, 不作为 `storage:` 配置键暴露。 */
    public const string Spill = "spill";

    /** 分区清单: 也是 `storage:` 段支持的键与迁移搬运清单。 */
    public static readonly IReadOnlyList<string> Names =
    [
        Sessions,
        Logs,
        Cache,
        Checkpoints,
        Attachments,
        ToolResults,
        Telemetry,
        Run,
    ];

    /** 分区默认值(缺省即分区名本身): telemetry 是文件而非目录, 默认值与改造前的 <root>/telemetry.jsonl 一致。 */
    private static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Telemetry] = "telemetry.jsonl",
    };

    /**
     * 解析全部分区。configured 为空或该键缺失即用同名默认目录(默认值与改造前布局一致)。
     * userProfile 供 `~` 展开注入(测试用); 为空取当前用户目录。
     */
    public static DshPaths Resolve(string root, IReadOnlyDictionary<string, string?>? configured = null, string? userProfile = null)
    {
        var fullRoot = System.IO.Path.GetFullPath(root);
        var partitions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Names)
        {
            var value = configured is not null && configured.TryGetValue(name, out var configuredValue)
                && !string.IsNullOrWhiteSpace(configuredValue)
                ? configuredValue.Trim()
                : Defaults.TryGetValue(name, out var fallback) ? fallback : name;
            var expanded = ExpandUser(value, userProfile);
            partitions[name] = System.IO.Path.IsPathRooted(expanded)
                ? System.IO.Path.GetFullPath(expanded)
                : System.IO.Path.Combine(fullRoot, expanded);
        }
        return new DshPaths(fullRoot, partitions);
    }

    public static DshPaths Default(string root) => Resolve(root);

    /**
     * 取本上下文使用的路径集: 启动时写入的 `dshPaths` 优先; 没有(独立组合/单元测试)时按 `dshHomePath`
     * 配默认分区兜底, 与改造前的布局一致; 两者都没有即返回 null, 由调用方沿用各自"未配置"的处理。
     */
    public static DshPaths? FromContext(Context ctx)
        => ctx.GetProp(PropName) as DshPaths
        ?? (ctx.GetProp("dshHomePath") is string { Length: > 0 } root ? Default(root) : null);

    /** 分区绝对路径; 未知分区按"相对根的同名目录"处理。 */
    public string Path(string name)
        => Partitions.TryGetValue(name, out var resolved) ? resolved : System.IO.Path.Combine(Root, name);

    public string SessionsPath => Path(Sessions);
    public string LogsPath => Path(Logs);
    public string CachePath => Path(Cache);
    public string CheckpointsPath => Path(Checkpoints);
    public string AttachmentsPath => Path(Attachments);
    public string ToolResultsPath => Path(ToolResults);
    public string SpillPath => Path(Spill);
    public string TelemetryPath => Path(Telemetry);
    public string RunPath => Path(Run);

    public string SettingsFile => System.IO.Path.Combine(Root, "settings.yaml");

    private static string ExpandUser(string value, string? userProfile)
    {
        if (value != "~" && !value.StartsWith("~/", StringComparison.Ordinal) && !value.StartsWith("~\\", StringComparison.Ordinal))
            return value;
        var profile = userProfile
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        return value.Length == 1 ? profile : System.IO.Path.Combine(profile, value[2..]);
    }
}
