namespace Dsh.Boot;

/**
 * Linux 终端入口注册: 让桌面环境/启动器唤起"终端"时进的是 dsharp 的独立 GPU 窗口形态。
 * 覆盖 freedesktop 的入口惯例(xdg-terminal-exec / $TERMINAL / x-terminal-emulator), 不碰任何系统级 hook。
 */
public static class TerminalEntryRegistration
{
    /** 包装脚本名: 会话 PATH 里通常包含 ~/.local/bin, 因此可作为 $TERMINAL 与 alternatives 的目标。 */
    public const string LauncherName = "dsh-terminal";

    public static async Task<int> RegisterAsync(TextWriter output)
    {
        if (!OperatingSystem.IsLinux())
        {
            await output.WriteLineAsync("dsharp: 终端入口注册只适用于 Linux(Windows 侧走自带的终端设置, 不做系统级注册)");
            return 1;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = XdgPath(home, "XDG_DATA_HOME", ".local/share");
        var configHome = XdgPath(home, "XDG_CONFIG_HOME", ".config");
        var binDirectory = Path.Combine(home, ".local", "bin");
        var launcher = Path.Combine(binDirectory, LauncherName);
        var desktopDirectory = Path.Combine(dataHome, "xdg-terminals");
        var desktopFile = Path.Combine(desktopDirectory, $"{LauncherName}.desktop");
        var listFile = Path.Combine(configHome, "xdg-terminals.list");
        var environmentFile = Path.Combine(configHome, "environment.d", "dsh-terminal.conf");

        Directory.CreateDirectory(binDirectory);
        Directory.CreateDirectory(desktopDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(environmentFile)!);
        await File.WriteAllTextAsync(launcher, LauncherScript, new System.Text.UTF8Encoding(false));
        SetExecutable(launcher);
        await File.WriteAllTextAsync(desktopFile, DesktopEntry, new System.Text.UTF8Encoding(false));
        await UpsertListAsync(listFile, $"{LauncherName}.desktop");
        await File.WriteAllTextAsync(environmentFile, $"TERMINAL={LauncherName}\n", new System.Text.UTF8Encoding(false));

        await output.WriteLineAsync($"dsharp: 已写入 {launcher}");
        await output.WriteLineAsync($"dsharp: 已写入 {desktopFile}");
        await output.WriteLineAsync($"dsharp: 已把 {LauncherName}.desktop 登记进 {listFile}(xdg-terminal-exec)");
        await output.WriteLineAsync($"dsharp: 已写入 {environmentFile}($TERMINAL)");
        await output.WriteLineAsync("dsharp: Debian 系若要接管 x-terminal-emulator, 以 root 执行:");
        await output.WriteLineAsync($"dsharp:   update-alternatives --install /usr/bin/x-terminal-emulator x-terminal-emulator {binDirectory}/x-terminal-emulator-dsh 40");
        await output.WriteLineAsync("dsharp: 其余桌面环境请在各自的默认终端设置里选择 dsharp(或本机路径 " + launcher + ")");
        return 0;
    }

    private const string LauncherScript = """
        #!/bin/sh
        # dsharp 终端入口: 交给独立 GPU 窗口形态, 启动即带真 shell 窗格(Dsh.Pty 宿主)。
        exec dsharp --gpu --shell "$@"
        """;

    /** xdg-terminal-exec 读取 .desktop 里的 Exec; Terminal=true 表示它本身就是终端。 */
    private const string DesktopEntry = """
        [Desktop Entry]
        Type=Application
        Name=DSH Terminal
        Comment=dsharp 终端(独立 GPU 窗口形态)
        Exec=dsharp --gpu --shell
        Terminal=true
        Categories=System;TerminalEmulator;
        """;

    private static async Task UpsertListAsync(string listFile, string entry)
    {
        var lines = File.Exists(listFile)
            ? (await File.ReadAllLinesAsync(listFile)).ToList()
            : [];
        if (!lines.Contains(entry, StringComparer.Ordinal))
            lines.Insert(0, entry);
        await File.WriteAllLinesAsync(listFile, lines, new System.Text.UTF8Encoding(false));
    }

    private static string XdgPath(string home, string variable, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(home, fallback)
            : configured;
    }

    private static void SetExecutable(string path)
    {
        // SetUnixFileMode 在 Windows 上不受支持; 本入口整体仅 Linux 可达, 这里显式守卫以标明平台边界
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception)
        {
            // 非 Unix 文件系统或权限受限时保持默认模式
        }
    }
}
