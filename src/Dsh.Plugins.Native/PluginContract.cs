namespace Dsh.Plugins.Native;

/** 原生插件的声明面:作者只声明包名与工具集合;
 *  导出握手、句柄表、两段式缓冲协议与异常拦截由生成器产出,作者不手写 ABI 胶水。 */
public interface IDshNativePlugin
{
    string Package { get; }

    IReadOnlyList<NativePluginTool> Tools { get; }

    /** 插件认哪些参数(默认空=不声明):生成器编成 dsh_plugin_settings_schema 导出,宿主据此传参并纳入文档。 */
    IReadOnlyList<NativePluginSetting> Settings => [];
}

/** 原生插件暴露给宿主的工具:ParametersJson 是 JSON Schema;Invoke 收参数 JSON,返回结果 JSON。 */
public sealed record NativePluginTool(
    string Name,
    string Description,
    string ParametersJson,
    Func<string, string?> Invoke);

/** 原生插件声明的一个配置项:Kind 用托管侧同名类型族(Text/Boolean/Integer/Number/List/Mapping);DefaultJson 是 JSON 文本。 */
public sealed record NativePluginSetting(
    string Key,
    string Kind,
    string? DefaultJson = null,
    string? Description = null);

/** 作者侧运行期入口:生成的导出层在激活时挂上日志接收器与工具登记表,作者代码可随时记日志、增删工具(未激活时静默)。 */
public static class DshNativePluginRuntime
{
    public static Action<int, string>? LogSink { get; set; }

    /** 生成器在 Activate/Deactivate 时挂接/摘除;作者用下方 RegisterTool/UnregisterTool 访问。 */
    public static INativeToolRegistry? ToolRegistry { get; set; }

    /** 生成器在 Activate 时挂接:作者用 Config 取自己在 settings.yaml 里的参数(JSON 文本)。 */
    public static Func<string>? ConfigSource { get; set; }

    /// <summary>Installed only for an active plugin by the generated ABI exports.</summary>
    public static Func<string, string, string>? HostCaller { get; set; }

    public static string CallHost(string operation, string argumentsJson)
        => (HostCaller ?? throw new InvalidOperationException("the native plugin is not active"))(operation, argumentsJson);

    public static void Log(int level, string message) => LogSink?.Invoke(level, message);

    /** 该插件在 settings.yaml 里的参数(JSON 文本);未激活或宿主不支持时为空串。 */
    public static string Config => ConfigSource?.Invoke() ?? "";

    /** 运行期登记一个工具,返回句柄;未激活或宿主不支持时返回 0。 */
    public static int RegisterTool(string name, string description, string parametersJson, Func<string, string?> invoke)
        => ToolRegistry?.RegisterTool(name, description, parametersJson, invoke) ?? 0;

    /** Removes a registered tool; an invalid handle returns false. */
    public static bool UnregisterTool(int handle)
        => ToolRegistry?.UnregisterTool(handle) == DshNativePluginAbi.Ok;
}
