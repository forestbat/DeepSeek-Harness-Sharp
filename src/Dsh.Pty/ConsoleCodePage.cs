using System.Runtime.InteropServices;

namespace Dsh.Pty;

/**
 * Windows 控制台代码页声明: dsharp 与终端之间走的是**原始 UTF-8 字节**, 而代码页决定控制台怎么解释这些字节。
 * 输出侧: daemon 用 FreeConsole 脱离终端后创建的 ConPTY 伪控制台会退回系统 OEM 代码页(简中 936), 于是托管 TUI 写出的
 * 中文与边框字符被误读成"é¹â¬"这类乱码、ESC 序列还会错位(同一段 ASCII 却完好)。
 * 输入侧: 代理从控制台读入的是按**输入代码页**编码的字节, 936 下汉字变成 GBK 两字节(如"测试"=B2E2CAD4), 送到会话里
 * 又按 UTF-8 解释 → 输入行与发出内容全是乱码; 终端本身发的、以及下游 ConPTY 期望的都是 UTF-8。
 * 两侧都声明成 UTF-8, 宿主/会话才按 UTF-8 解释。Unix 无此概念, 直接跳过。
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
            SetConsoleCP(Utf8CodePage);
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint wCodePageId);
}
