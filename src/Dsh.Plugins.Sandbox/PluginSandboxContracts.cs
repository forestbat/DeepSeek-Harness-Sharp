using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace Dsh.Plugins.Sandbox;

/** 沙箱线格式: 沿用既有 camelCase 的源生成上下文, 双端各拼一份同样的解析器。 */
public static class PluginSandboxWire
{
    public static IJsonRpcMessageFormatter Formatter()
        => new SystemTextJsonFormatter
        {
            // 上下文自带的 Options 与它生成的元数据绑定, 复制或直接复用都会让元数据指向别的实例;
            // 这里另建一份, 只把上下文当解析器用。
            JsonSerializerOptions = new JsonSerializerOptions
            {
                TypeInfoResolver = PluginSandboxJsonContext.Default,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            },
        };
}
