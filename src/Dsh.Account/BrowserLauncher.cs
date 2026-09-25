using System.Diagnostics;

namespace Dsh.Account;

/** 用系统默认浏览器打开平台授权页; 失败返回 false 由调用方诊断, 不抛异常。 */
public static class BrowserLauncher
{
    public static bool Open(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", [url]);
            else
                Process.Start("xdg-open", [url]);
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}
