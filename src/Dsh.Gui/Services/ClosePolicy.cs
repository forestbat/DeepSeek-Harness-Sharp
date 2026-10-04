using System.Diagnostics;

namespace Dsh.Gui.Services;

public enum CloseActionKind
{
    Tray,
    Quit,
    Ask,
}

/**
 * 关闭行为与托盘能力。
 * Linux 上托盘图标走 StatusNotifier(SNI), 需要会话总线里真的有就绪的 watcher 对象:
 * KDE 由 kded 的 statusnotifierwatcher 模块提供(按需加载), GNOME 由 AppIndicator 扩展提供, Ubuntu 桌面默认带。
 * 探测用 gdbus / dbus-send / busctl 读 watcher 的 RegisteredStatusNotifierItems;
 * 名字存在但对象尚未导出的窗口期(刚登录桌面时)判为不可用, 否则 Avalonia 的 DBus 托盘会在异步回调里抛异常把进程带走。
 * 三个工具都不可用时, 退回按 XDG_CURRENT_DESKTOP 的名称启发式判断。
 */
public static class ClosePolicy
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string WatcherInterface = "org.kde.StatusNotifierWatcher";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string HostProperty = "IsStatusNotifierHostRegistered";
    private const int ProbeTimeoutMilliseconds = 2000;

    /** watcher 名字/对象缺失时的报错特征, 命中即判定为「当前桌面没有托盘」。 */
    private static readonly string[] UnsupportedErrors =
        ["UnknownObject", "ServiceUnknown", "UnknownMethod", "NameHasNoOwner", "no such object", "unknown object", "unknown service"];

    private static readonly (string Command, string[] Arguments)[] Probes =
    [
        ("gdbus",
        [
            "call", "--session", "--dest", WatcherName, "--object-path", WatcherPath,
            "--method", $"{PropertiesInterface}.Get", WatcherInterface, HostProperty,
        ]),
        ("dbus-send",
        [
            "--session", "--print-reply", $"--dest={WatcherName}", WatcherPath,
            $"{PropertiesInterface}.Get", $"string:{WatcherInterface}", $"string:{HostProperty}",
        ]),
        ("busctl",
        [
            "--user", "call", WatcherName, WatcherPath, PropertiesInterface, "Get", "ss", WatcherInterface, HostProperty,
        ]),
    ];

    public static bool TraySupported { get; } = DetectTraySupport();

    public static CloseActionKind Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        GuiSettings.CloseQuit => CloseActionKind.Quit,
        GuiSettings.CloseAsk => CloseActionKind.Ask,
        _ => CloseActionKind.Tray,
    };

    public static string Wire(CloseActionKind kind) => kind switch
    {
        CloseActionKind.Quit => GuiSettings.CloseQuit,
        CloseActionKind.Ask => GuiSettings.CloseAsk,
        _ => GuiSettings.CloseTray,
    };

    /** 请求的行为在当前环境是否可行: 没有托盘宿主时「最小化到托盘」退化成直接退出, 免得窗口藏进看不见的地方。 */
    public static CloseActionKind Coerce(CloseActionKind requested, bool traySupported)
        => requested == CloseActionKind.Tray && !traySupported ? CloseActionKind.Quit : requested;

    public static CloseActionKind Effective(CloseActionKind requested) => Coerce(requested, TraySupported);

    /** 会话总线里有没有就绪的 StatusNotifier 宿主; null 表示探测不出来(gdbus/dbus-send/busctl 都不可用或连不上总线)。 */
    public static bool? ProbeWatcher()
    {
        foreach (var probe in Probes)
        {
            if (RunProbe(probe) is not { } run)
                continue;
            if (run.ExitCode != 0)
            {
                if (UnsupportedErrors.Any(token => run.Text.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    return false;
                continue;
            }
            if (run.Text.Contains("true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (run.Text.Contains("false", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return null;
    }

    /** 关闭当下复查: 桌面托盘宿主可能已经退出, 这时不能再把窗口藏进看不见的地方。 */
    public static bool TrayAvailable()
    {
        if (!OperatingSystem.IsLinux())
            return true;
        return ProbeWatcher() ?? TraySupported;
    }

    /** 探测不可用时的名称启发式: 除 GNOME(Ubuntu/Unity 系例外)外, 常见桌面默认都带托盘宿主。 */
    public static bool DesktopHasTrayByName(string? desktopName = null)
    {
        var desktop = desktopName ?? Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        if (!desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase))
            return true;
        return desktop.Contains("ubuntu", StringComparison.OrdinalIgnoreCase) || desktop.Contains("unity", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DetectTraySupport()
    {
        if (!OperatingSystem.IsLinux())
            return true;
        return ProbeWatcher() ?? DesktopHasTrayByName();
    }

    private readonly record struct ProbeRun(int ExitCode, string Text);

    private static ProbeRun? RunProbe((string Command, string[] Arguments) probe)
    {
        try
        {
            var startInfo = new ProcessStartInfo(probe.Command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in probe.Arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(ProbeTimeoutMilliseconds))
            {
                process.Kill(true);
                return null;
            }
            return new ProbeRun(process.ExitCode, output.Length > 0 ? output : error);
        }
        catch (Exception)
        {
            // 命令不存在或会话总线连不上: 换下一个探测器, 全部失败再退回名称启发式。
            return null;
        }
    }
}
