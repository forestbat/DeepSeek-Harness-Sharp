namespace Dsh.Gui.Host;

/** GUI 启动器入口:把参数原样转给组合根的 `gui` 分支。 */
public static class Program
{
    /** GUI 按需拉起本机 host 时以自身可执行文件运行 `host serve`(HostAutostart.ServeCommand 取当前进程路径), 这一子命令原样转给组合根。 */
    private const string HostCommand = "host";

    private const string TuiCommand = "tui";

    [STAThread]
    public static int Main(string[] args)
        => Dsh.Host.HarnessEntrypoint.RunAsync(args is [HostCommand, ..] or [TuiCommand, ..] ? args : ["gui", .. args]).GetAwaiter().GetResult();
}
