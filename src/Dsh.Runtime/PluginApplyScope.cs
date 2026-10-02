namespace Dsh.Runtime;

/**
 * 当前正在执行 Apply 的插件名:注册面(如 ToolRuntime)借此把注册归属到正确的包,用于重名消解与 WARN 归属。
 * Apply 之外的注册(宿主根、桥线程回调)读不到名字,归属为 null。
 */
public static class PluginApplyScope
{
    private static readonly AsyncLocal<string?> CurrentName = new();

    public static string? CurrentPlugin => CurrentName.Value;

    internal static void Enter(string name) => CurrentName.Value = name;

    internal static void Exit() => CurrentName.Value = null;
}
