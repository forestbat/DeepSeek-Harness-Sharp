using System.Diagnostics;

namespace Dsh.Account;

/**
 * 用系统默认浏览器打开平台授权页; 失败返回 false 由调用方诊断, 不抛异常。
 * 桌面走 open/xdg-open/ShellExecute; Android 这类没有上述命令的宿主在组合前替换 Override,
 * 因为它们的"打开浏览器"是 Intent, 不是起一个进程。
 */
public static class BrowserLauncher
{
    /** 宿主注入的打开方式; 为 null 时按操作系统默认实现。 */
    public static Func<string, bool>? Override { get; set; }

    public static bool Open(string url)
    {
        if (Override is { } open)
            return open(url);
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else
            {
                var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
                {
                    ArgumentList = { url },
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var process = Process.Start(info);
                if (process is null)
                    return false;
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}