using System.Diagnostics;
using System.Text;

namespace Dsh.Tui;

/**
 * 从 OS 剪贴板读取图片字节(仅本机有剪贴板时可读): Linux 用 wl-paste/xclip, Windows 用 PowerShell(WinForms)。
 * SSH/无头会话里读不到终端另一端的剪贴板, 此时返回 false, 由"粘贴本地图片路径"路线兜底。
 */
internal static class ClipboardImage
{
    private const int TimeoutMs = 2000;

    public static bool TryRead(out byte[] bytes, out string mediaType)
    {
        bytes = [];
        mediaType = "image/png";
        try
        {
            if (OperatingSystem.IsLinux())
                return TryReadLinux(out bytes);
            if (OperatingSystem.IsWindows())
                return TryReadWindows(out bytes, out mediaType);
        }
        catch (Exception)
        {
        }
        return false;
    }

    private static bool TryReadLinux(out byte[] bytes)
    {
        if (TryCapture("wl-paste", ["--no-newline", "--type", "image/png"], out bytes))
            return true;
        return TryCapture("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"], out bytes);
    }

    private static bool TryReadWindows(out byte[] bytes, out string mediaType)
    {
        mediaType = "image/png";
        const string script =
            "Add-Type -AssemblyName System.Windows.Forms; $i=[System.Windows.Forms.Clipboard]::GetImage(); "
            + "if($i){$m=New-Object System.IO.MemoryStream; $i.Save($m,[System.Drawing.Imaging.ImageFormat]::Png); "
            + "[Convert]::ToBase64String($m.ToArray())}";
        if (!TryCapture("powershell.exe", ["-NoProfile", "-STA", "-Command", script], out var encoded))
        {
            bytes = [];
            return false;
        }
        try
        {
            bytes = Convert.FromBase64String(Encoding.ASCII.GetString(encoded).Trim());
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
        return bytes.Length > 0;
    }

    private static bool TryCapture(string fileName, IReadOnlyList<string> arguments, out byte[] bytes)
    {
        bytes = [];
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start())
                return false;
            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            if (!process.WaitForExit(TimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                return false;
            }
            if (process.ExitCode != 0 || buffer.Length == 0)
                return false;
            bytes = buffer.ToArray();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
