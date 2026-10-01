using System.Runtime.InteropServices;

namespace Dsh.Pty;

/**
 * Windows 控制台输出代码页声明: dsh 向终端写出的是**原始 UTF-8 字节**, 而控制台的输出代码页决定宿主怎么解释这些字节。
 * daemon 用 FreeConsole 脱离终端后创建的 ConPTY 伪控制台会退回系统 OEM 代码页(简中 936), 于是托管 TUI 写出的中文与
 * 边框字符被误读成"é¹â¬"这类乱码、ESC 序列还会错位(同一段 ASCII 却完好)。写出方在渲染/隧道前把本控制台代码页设为
 * UTF-8, 宿主才会按 UTF-8 解释。Unix 无此概念, 直接跳过。
 */
internal static class ConsoleCodePage
{
    private const uint Utf8CodePage = 65001;

    public static void EnsureUtf8()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            SetConsoleOutputCP(Utf8CodePage);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint wCodePageId);
}
