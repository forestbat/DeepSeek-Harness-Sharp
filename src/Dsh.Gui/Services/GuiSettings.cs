using Dsh.Boot;

namespace Dsh.Gui.Services;

/**
 * GUI 插件自己的参数段: settings.yaml -> plugins."@deepseek-ai/dsh-gui"。
 * 参数缺失一律退回默认值, 不因手改配置把界面卡住; 写回走 HarnessSettings.SavePlugins, 保留文件其余内容。
 */
public sealed class GuiSettings(HarnessHome home)
{
    public const string Package = "@deepseek-ai/dsh-gui";

    public const string ThemeDark = "dark";
    public const string ThemeLight = "light";
    public const string ThemeSystem = "system";
    public const string CloseTray = "tray";
    public const string CloseQuit = "quit";
    public const string CloseAsk = "ask";
    public const string ViewSolution = "solution";
    public const string ViewFilesystem = "filesystem";
    public const string SortUpdated = "updated";
    public const string SortName = "name";
    public const string TraceAll = "all";

    public const double MinFontSize = 11;
    public const double MaxFontSize = 18;

    public event Action? Changed;

    public GuiSettingsSnapshot Load()
        => GuiSettingsSnapshot.From(Parameters());

    public void Save(GuiSettingsSnapshot snapshot)
    {
        var settings = HarnessSettings.Load(home);
        var existing = settings.Plugins.GetValueOrDefault(Package);
        settings.Plugins[Package] = new PluginSetting
        {
            Enabled = existing?.Enabled ?? true,
            Parameters = snapshot.ToParameters(),
        };
        settings.SavePlugins(home);
        Changed?.Invoke();
    }

    private Dictionary<string, object?> Parameters()
        => HarnessSettings.Load(home).Plugins.GetValueOrDefault(Package)?.Parameters ?? [];
}

/** SSH 认证方式。 */
public static class SshAuth
{
    public const string Password = "password";
    public const string Key = "key";
    public const string Agent = "agent";
}

/** 用户保存的 SSH 远程工作区: 名称 + 连接参数 + 认证方式 + 可选代理/远端目录。 */
public sealed record SshWorkspace(
    string Name,
    string Host,
    int Port,
    string User,
    string Auth,
    string? KeyPath = null,
    string? Proxy = null,
    string? RemotePath = null,
    string? Password = null);

/** 参数快照: 默认值即「零配置可用」的形态。 */
public sealed record GuiSettingsSnapshot
{
    public string Theme { get; init; } = GuiSettings.ThemeDark;

    public string Language { get; init; } = "zh-CN";

    public double FontSize { get; init; } = 13.5;

    public string WorkspaceView { get; init; } = GuiSettings.ViewSolution;

    /** 新会话的默认工作区(绝对路径); null 表示跟随启动目录。已有会话的 cwd 不变。 */
    public string? DefaultWorkspace { get; init; }

    /** 用户保存的 SSH 远程工作区(工作区菜单“远程工作区”与设置页“远程”共用)。 */
    public IReadOnlyList<SshWorkspace> RemoteWorkspaces { get; init; } = [];

    public string SortSessions { get; init; } = GuiSettings.SortUpdated;

    public bool ShowOnlyWithSessions { get; init; }

    public string TraceFilter { get; init; } = GuiSettings.TraceAll;

    public string CloseAction { get; init; } = GuiSettings.CloseTray;

    public bool SidebarVisible { get; init; } = true;

    public double SidebarWidth { get; init; } = 300;

    public bool GpuEnabled { get; init; } = true;

    public string GpuBackend { get; init; } = "auto";

    public string GpuAdapter { get; init; } = "auto";

    public bool RememberBounds { get; init; } = true;

    public double WindowWidth { get; init; } = 1440;

    public double WindowHeight { get; init; } = 900;

    public double? WindowX { get; init; }

    public double? WindowY { get; init; }

    public bool WindowMaximized { get; init; }

    public static GuiSettingsSnapshot From(IReadOnlyDictionary<string, object?> parameters)
    {
        var gpu = Map(parameters, "gpu");
        var window = Map(parameters, "window");
        return new GuiSettingsSnapshot
        {
            Theme = Text(parameters, "theme") ?? GuiSettings.ThemeDark,
            Language = Text(parameters, "language") ?? "zh-CN",
            FontSize = Clamp(Number(parameters, "fontSize") ?? 13.5, GuiSettings.MinFontSize, GuiSettings.MaxFontSize),
            WorkspaceView = Text(parameters, "workspaceView") ?? GuiSettings.ViewSolution,
            DefaultWorkspace = Text(parameters, "defaultWorkspace"),
            RemoteWorkspaces = Workspaces(parameters, "remoteWorkspaces"),
            SortSessions = Text(parameters, "sortSessions") ?? GuiSettings.SortUpdated,
            ShowOnlyWithSessions = Flag(parameters, "showOnlyWithSessions") ?? false,
            TraceFilter = Text(parameters, "traceFilter") ?? GuiSettings.TraceAll,
            CloseAction = Text(parameters, "closeAction") ?? GuiSettings.CloseTray,
            SidebarVisible = Flag(parameters, "sidebarVisible") ?? true,
            SidebarWidth = Clamp(Number(parameters, "sidebarWidth") ?? 300, 200, 480),
            GpuEnabled = Flag(gpu, "enabled") ?? true,
            GpuBackend = Text(gpu, "backend") ?? "auto",
            GpuAdapter = Text(gpu, "adapter") ?? "auto",
            RememberBounds = Flag(window, "rememberBounds") ?? true,
            WindowWidth = Clamp(Number(window, "width") ?? 1440, 900, 8000),
            WindowHeight = Clamp(Number(window, "height") ?? 900, 600, 8000),
            WindowX = Number(window, "x"),
            WindowY = Number(window, "y"),
            WindowMaximized = Flag(window, "maximized") ?? false,
        };
    }

    public Dictionary<string, object?> ToParameters() => new(StringComparer.Ordinal)
    {
        ["theme"] = Theme,
        ["language"] = Language,
        ["fontSize"] = FontSize,
        ["workspaceView"] = WorkspaceView,
        ["defaultWorkspace"] = DefaultWorkspace,
        ["remoteWorkspaces"] = RemoteWorkspaces
            .Select(workspace => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = workspace.Name,
                ["host"] = workspace.Host,
                ["port"] = workspace.Port,
                ["user"] = workspace.User,
                ["auth"] = workspace.Auth,
                ["keyPath"] = workspace.KeyPath,
                ["proxy"] = workspace.Proxy,
                ["remotePath"] = workspace.RemotePath,
                ["password"] = workspace.Password,
            })
            .ToList(),
        ["sortSessions"] = SortSessions,
        ["showOnlyWithSessions"] = ShowOnlyWithSessions,
        ["traceFilter"] = TraceFilter,
        ["closeAction"] = CloseAction,
        ["sidebarVisible"] = SidebarVisible,
        ["sidebarWidth"] = SidebarWidth,
        ["gpu"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["enabled"] = GpuEnabled,
            ["backend"] = GpuBackend,
            ["adapter"] = GpuAdapter,
        },
        ["window"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rememberBounds"] = RememberBounds,
            ["width"] = WindowWidth,
            ["height"] = WindowHeight,
            ["x"] = WindowX,
            ["y"] = WindowY,
            ["maximized"] = WindowMaximized,
        },
    };

    private static IReadOnlyDictionary<string, object?> Map(IReadOnlyDictionary<string, object?> source, string key)
        => source.GetValueOrDefault(key) as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>();

    private static string? Text(IReadOnlyDictionary<string, object?> source, string key)
        => source.GetValueOrDefault(key) is string text && text.Length > 0 ? text : null;

    private static bool? Flag(IReadOnlyDictionary<string, object?> source, string key)
        => source.GetValueOrDefault(key) as bool?;

    private static double? Number(IReadOnlyDictionary<string, object?> source, string key)
        => source.GetValueOrDefault(key) switch
        {
            double value => value,
            long value => value,
            int value => value,
            _ => null,
        };

    private static double Clamp(double value, double minimum, double maximum)
        => Math.Clamp(value, minimum, maximum);

    private static IReadOnlyList<SshWorkspace> Workspaces(IReadOnlyDictionary<string, object?> source, string key)
    {
        if (source.GetValueOrDefault(key) is not IEnumerable<object?> items)
            return [];
        var result = new List<SshWorkspace>();
        foreach (var item in items)
        {
            if (item is not IReadOnlyDictionary<string, object?> map)
                continue;
            var name = Text(map, "name");
            var host = Text(map, "host");
            if (name is null || host is null)
                continue;
            result.Add(new SshWorkspace(
                name,
                host,
                (int)(Number(map, "port") ?? 22),
                Text(map, "user") ?? "",
                Text(map, "auth") ?? SshAuth.Agent,
                Text(map, "keyPath"),
                Text(map, "proxy"),
                Text(map, "remotePath"),
                Text(map, "password")));
        }

        return result;
    }
}
