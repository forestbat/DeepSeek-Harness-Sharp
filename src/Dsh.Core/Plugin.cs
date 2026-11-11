using Dsh.Runtime;
using Dsh.Llm;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Core.Plugin.Core)]

namespace Dsh.Core;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Core = "@deepseek-ai/dsh-core";

    public string[] Inject => packageName switch
    {
        Core => [],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Core => RegisterCore(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    /** 会话内核只装配存储与 LLM 运行时; agent 循环与工具运行时由 @deepseek-ai/dsh-agent 装配。 */
    private static IDisposable RegisterCore(Context ctx, object? config)
    {
        _ = new SessionStore(ctx);
        _ = new FileAttachmentStore(ctx);
        _ = new LlmRuntime(ctx, LlmStreamLimitsFrom(config));
        _ = new LlmAdapterFactoryRegistry(ctx);
        return new NoopDisposable();
    }

    /** 流式看门狗阈值: 默认对齐 qwen-code (idle 4 分钟 / lifetime 15 分钟); 非正值关闭该守卫。 */
    private static LlmStreamLimits LlmStreamLimitsFrom(object? config)
    {
        var dict = config as IReadOnlyDictionary<string, object?>;
        return LlmStreamLimits.Resolve(
            IntOf(dict, "streamIdleTimeoutMs"),
            IntOf(dict, "streamMaxLifetimeMs"));
    }

    private static int? IntOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => (int)value,
            int value => value,
            _ => null,
        };

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
