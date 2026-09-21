using System.Runtime.InteropServices;

namespace Dsh.Host;

/** 控制台窗口处理: 双击启动时进程独占控制台, 需要把它去掉, 否则 GUI 会顶着一个黑窗口。 */
internal static class ConsoleWindow
{
    /** 只在自己的控制台上生效; 从终端启动(dsh gui)时控制台属于用户, 必须保留。 */
    public static void DetachIfOwned()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var window = GetConsoleWindow();
        if (window == IntPtr.Zero)
            return;
        var processes = new uint[ProcessProbeSize];
        if (GetConsoleProcessList(processes, (uint)processes.Length) != 1)
            return;
        FreeConsole();
    }

    private const int ProcessProbeSize = 8;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
