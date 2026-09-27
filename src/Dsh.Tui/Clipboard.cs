using System.ComponentModel;
using System.Diagnostics;

namespace Dsh.Tui;

/** 系统剪贴板写入: 逐候选命令 best-effort(WSL 经 clip.exe 互通, Linux 桌面 wl-copy/xclip/xsel, macOS pbcopy)。 */
internal static class Clipboard
{
    private static readonly string[][] Candidates = OperatingSystem.IsWindows()
        ? [["clip.exe"]]
        :
        [
            ["wl-copy"],
            ["xclip", "-selection", "clipboard"],
            ["xsel", "--clipboard", "--input"],
            ["pbcopy"],
            ["/mnt/c/Windows/System32/clip.exe"],
        ];

    public static async Task<bool> TrySetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var candidate in Candidates)
        {
            if (await RunAsync(candidate[0], candidate[1..], text).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    private static async Task<bool> RunAsync(string fileName, IReadOnlyList<string> arguments, string text)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true } };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            if (!process.Start())
                return false;
            await process.StandardInput.WriteAsync(text).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
