using System.Diagnostics;
using System.Runtime.Versioning;

namespace Dsh.Gui.Services;

public static class DesktopIntegration
{
    private const string ShortcutName = "DeepSeek Harness";
    private const string DesktopEntryName = "dsh-gui.desktop";
    private const string IconIndex = ",0";

    public static string CreateShortcut(string targetPath, string? iconPath, string? arguments = null)
    {
        if (OperatingSystem.IsWindows())
            return CreateWindowsShortcut(targetPath, iconPath, arguments);
        if (OperatingSystem.IsLinux())
            return CreateLinuxEntry(targetPath, iconPath, arguments);
        return $"当前平台不支持创建快捷方式: {targetPath}";
    }

    // 资源管理器被第三方工具(Tablacus 等)接管时, shell 钩子会挂起 UseShellExecute 的同步调用, 必须离开 UI 线程。
    public static async Task<string> OpenPathAsync(string path)
        => await Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return "已打开";
                }
                var command = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
                var startInfo = new ProcessStartInfo(command) { UseShellExecute = false };
                startInfo.ArgumentList.Add(path);
                Process.Start(startInfo);
                return "已打开";
            }
            catch (Exception error)
            {
                return $"打开失败: {error.Message}";
            }
        });

    [SupportedOSPlatform("windows")]
    private static string CreateWindowsShortcut(string targetPath, string? iconPath, string? arguments)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (desktop.Length == 0)
            return "未找到桌面目录，无法创建快捷方式";
        var linkPath = Path.Combine(desktop, $"{ShortcutName}.lnk");
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return "未找到 WScript.Shell，无法创建快捷方式";
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = targetPath;
            link.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(arguments))
                link.Arguments = arguments;
            if (!string.IsNullOrWhiteSpace(iconPath))
                link.IconLocation = iconPath.Contains(',') ? iconPath : $"{iconPath}{IconIndex}";
            link.Save();
            return $"已创建: {linkPath}";
        }
        catch (Exception error)
        {
            return $"创建快捷方式失败: {error.Message}";
        }
    }

    [SupportedOSPlatform("linux")]
    private static string CreateLinuxEntry(string targetPath, string? iconPath, string? arguments)
    {
        var applications = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications");
        try
        {
            Directory.CreateDirectory(applications);
            var entryPath = Path.Combine(applications, DesktopEntryName);
            var content = DesktopEntryContent(targetPath, iconPath, arguments);
            File.WriteAllText(entryPath, content);
            MakeExecutable(entryPath);
            WriteDesktopCopy(content);
            return $"已创建: {entryPath}";
        }
        catch (Exception error)
        {
            return $"创建桌面入口失败: {error.Message}";
        }
    }

    [SupportedOSPlatform("linux")]
    private static void WriteDesktopCopy(string content)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (desktop.Length == 0 || !Directory.Exists(desktop))
            return;
        var copyPath = Path.Combine(desktop, DesktopEntryName);
        File.WriteAllText(copyPath, content);
        MakeExecutable(copyPath);
    }

    [SupportedOSPlatform("linux")]
    private static void MakeExecutable(string path)
        => File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

    private static string DesktopEntryContent(string targetPath, string? iconPath, string? arguments)
    {
        var exec = string.IsNullOrWhiteSpace(arguments) ? Quote(targetPath) : $"{Quote(targetPath)} {arguments.Trim()}";
        var icon = string.IsNullOrWhiteSpace(iconPath) ? string.Empty : $"Icon={iconPath}\n";
        return $"""
            [Desktop Entry]
            Type=Application
            Name={ShortcutName}
            Comment=DeepSeek Harness GUI
            Exec={exec}
            {icon}Terminal=false
            Categories=Development;

            """;
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
