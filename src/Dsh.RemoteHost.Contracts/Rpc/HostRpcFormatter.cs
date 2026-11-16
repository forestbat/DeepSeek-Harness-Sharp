using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using StreamJsonRpc;

namespace Dsh.RemoteHost.Rpc;

/**
 * 线格式: 沿用仓库已有的源生成 JSON 上下文, 不引入第二套序列化栈。
 *
 * 基础上下文覆盖远端工作区自有的 DTO; 依赖方向不允许 Contracts 反向引用 Dsh.Ide, 所以编辑器域那套上下文
 * 由调用方通过 extra 传进来, 两侧各自拼装同一份解析器。
 */
public static class HostRpcFormatter
{
    public static IJsonRpcMessageFormatter CreateJson(params IJsonTypeInfoResolver[] extra)
    {
        IJsonTypeInfoResolver baseResolver = JsonTypeInfoResolver.Combine(
            HostProtocolJsonContext.Default,
            AgentProtocolJsonContext.Default,
            FilesProtocolJsonContext.Default,
            GitProtocolJsonContext.Default,
            NetworkProtocolJsonContext.Default,
            NotificationsProtocolJsonContext.Default,
            PluginsProtocolJsonContext.Default,
            PtyProtocolJsonContext.Default,
            SessionEditingProtocolJsonContext.Default);
        return new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions
            {
                // 源生成上下文用的是 DTO 上的属性名(PascalCase), 不用 StreamJsonRpc 默认的 camelCase:
                // 既有线协议与旧版长度帧完全一样, 新旧两端才能互认。
                PropertyNamingPolicy = null,
                DictionaryKeyPolicy = null,
                TypeInfoResolver = extra.Length == 0 ? baseResolver : JsonTypeInfoResolver.Combine([baseResolver, .. extra]),
            },
        };
    }
}
