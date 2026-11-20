using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Dsh.Tui;

/**
 * 系统剪贴板写入。
 *
 * Windows 直接写 CF_UNICODETEXT: 文本本来就是 UTF-16, 中间不经过任何代码页。
 * 旧实现把文本重定向给 clip.exe 的 stdin, 而 .NET 的 StandardInput 默认走控制台代码页(中文 Windows = GBK/CP936),
 * UTF-8/UTF-16 的中文一到剪贴板就变成乱码。
 *
 * 其它平台仍按候选命令 best-effort(WSL 经 clip.exe 互通, Linux 桌面 wl-copy/xclip/xsel, macOS pbcopy),
 * 但这些工具按 UTF-8 解释输入, 必须显式声明, 不能依赖默认值。
 */
internal static partial class Clipboard
{
    private const uint GmemMoveable = 0x0002;
    private const uint CfUnicodeText = 13;

    private static readonly string[][] Candidates = OperatingSystem.IsWindows()
        ? [["clip.exe"]]
        :
        [
            ["wl-copy"],
            ["xclip", "-selection", "clipboard"],
            ["xsel", "--clipboard", "--input"],
            ["pbcopy"],
            ["/mnt/c/Windows/System32/clip.exe"]
        ];

    public static async Task<bool> TrySetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        if (OperatingSystem.IsWindows() && TrySetUnicodeText(text))
            return true;
        foreach (var candidate in Candidates)
        {
            if (await RunAsync(candidate[0], candidate[1..], text).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    /** 直接把 UTF-16 文本放进剪贴板, 不经过任何代码页转换。 */
    private static bool TrySetUnicodeText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return false;
        try
        {
            if (!EmptyClipboard())
                return false;
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var handle = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
            if (handle == IntPtr.Zero)
                return false;
            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            try
            {
                Marshal.Copy(bytes, 0, target, bytes.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }
            if (SetClipboardData(CfUnicodeText, handle) != IntPtr.Zero)
                // 成功后内存所有权转给系统, 这里不能再 GlobalFree。
                return true;
            GlobalFree(handle);
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static async Task<bool> RunAsync(string fileName, IReadOnlyList<string> arguments, string text)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            // 这些工具按 UTF-8 读 stdin; 默认值会用控制台代码页, 中文因此变乱码。
            process.StartInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial void CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetClipboardData(uint format, IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void GlobalUnlock(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void GlobalFree(IntPtr memory);
}
