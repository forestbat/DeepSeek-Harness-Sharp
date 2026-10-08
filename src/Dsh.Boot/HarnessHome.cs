namespace Dsh.Boot;

/** 唯一持久化根。优先级: storage.root(settings) > $DSH_HOME > ~/.dsh。所有模块的数据子目录都由这里派生, 不再各自拼路径。 */
public sealed record HarnessHome(string Root)
{
    public const string HomeEnv = "DSH_HOME";

    /** daemon 托管会话时注入的 harness 根; 与 DSH_HOME(daemon 的 run 根)区分, 见 Dsh.Pty.PtySessionProtocol.ChildHomeVariable。 */
    public const string ChildHomeEnv = "DSH_HARNESS_HOME";

    public const string StorageRootMarker = ".storage-root";

    public static HarnessHome Resolve(string? storageRoot = null)
    {
        var root = storageRoot
            ?? Environment.GetEnvironmentVariable(ChildHomeEnv)
            ?? Environment.GetEnvironmentVariable(HomeEnv)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        return new HarnessHome(Path.GetFullPath(root));
    }

    /** 默认根: 只认 $DSH_HOME / ~/.dsh, 不看 settings(settings 本身就住在默认根里)。 */
    public static HarnessHome Default() => Resolve();

    public string SessionsPath => SubPath("sessions");

    public string LogsPath => SubPath("logs");

    public string CachePath => SubPath("cache");

    public string CheckpointsPath => SubPath("checkpoints");

    public string AttachmentsPath => SubPath("attachments");

    public string ToolResultsPath => SubPath("tool-results");

    public string RunPath => SubPath("run");

    public string TelemetryPath => SubPath("telemetry");

    public string AgentPresetsPath => SubPath(".agent-presets");

    public string SettingsFile => Path.Combine(Root, "settings.yaml");

    public string SubPath(string segment) => Path.Combine(Root, segment);

    /** 需要在根内自成一区的子目录, 迁移时按此清单搬运。 */
    public IReadOnlyList<string> DataDirectories =>
    [
        "sessions",
        "checkpoints",
        "attachments",
        "tool-results",
        "telemetry",
        "run",
        "logs",
        "cache",
        ".agent-presets"
    ];

    public void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SessionsPath);
        Directory.CreateDirectory(LogsPath);
    }
}
