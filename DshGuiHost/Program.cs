namespace Dsh.Gui.Host;

/** GUI 启动器入口:把参数原样转给组合根的 `gui` 分支。 */
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
        => Dsh.Host.HarnessEntrypoint.RunAsync(["gui", .. args]).GetAwaiter().GetResult();
}
