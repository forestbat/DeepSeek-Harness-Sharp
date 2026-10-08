namespace Dsh.Plugins.Native;

/** 原生插件的声明面:作者只声明包名与工具集合;
 *  导出握手、句柄表、两段式缓冲协议与异常拦截由生成器产出,作者不手写 ABI 胶水。 */
public interface IDshNativePlugin
{
    string Package { get; }

    IReadOnlyList<NativePluginTool> Tools { get; }
}

/** 原生插件暴露给宿主的工具:ParametersJson 是 JSON Schema;Invoke 收参数 JSON,返回结果 JSON。 */
public sealed record NativePluginTool(
    string Name,
    string Description,
    string ParametersJson,
    Func<string, string?> Invoke);

/** 作者侧运行期入口:生成的导出层在激活时挂上日志接收器与工具登记表,作者代码可随时记日志、增删工具(未激活时静默)。 */
public static class DshNativePluginRuntime
{
    public static Action<int, string>? LogSink { get; set; }

    /** 生成器在 Activate/Deactivate 时挂接/摘除;作者用下方 RegisterTool/UnregisterTool 访问。 */
    public static INativeToolRegistry? ToolRegistry { get; set; }

    public static void Log(int level, string message) => LogSink?.Invoke(level, message);

    /** 运行期登记一个工具,返回句柄;未激活或宿主不支持时返回 0。 */
    public static int RegisterTool(string name, string description, string parametersJson, Func<string, string?> invoke)
        => ToolRegistry?.RegisterTool(name, description, parametersJson, invoke) ?? 0;

    /** 运行期撤销一个工具;宿主不支持(旧 ABI)或句柄无效时返回 false。 */
    public static bool UnregisterTool(int handle)
        => ToolRegistry?.UnregisterTool(handle) == DshNativePluginAbi.Ok;
}
