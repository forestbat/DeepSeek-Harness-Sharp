using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Agent;

[assembly: DshPlugin(Dsh.E2b.Plugin.E2b)]
[assembly: DshPlugin(Dsh.E2b.Plugin.ToolE2b)]

namespace Dsh.E2b;

/** E2B 沙盒:@deepseek-ai/dsh-e2b 提供沙盒服务,@deepseek-ai/dsh-tool-e2b 暴露 e2b_run 工具。
 *  没配 apiKey(内联或环境变量)时服务插件自检跳过,工具包保持 pending,不向模型暴露不可用工具。 */
public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string E2b = "@deepseek-ai/dsh-e2b";
    internal const string ToolE2b = "@deepseek-ai/dsh-tool-e2b";

    public string[] Inject => packageName switch
    {
        E2b => [],
        ToolE2b => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, E2bRuntime.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        E2b => ApplyRuntime(ctx, config),
        ToolE2b => ToolE2bTool.Apply(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable ApplyRuntime(Context ctx, object? config)
    {
        var resolved = E2bConfig.From(config);
        if (string.IsNullOrWhiteSpace(resolved.ResolveApiKey()))
        {
            ctx.Logger.Warn("%s",
                $"@deepseek-ai/dsh-e2b is not activated: no API key "
                + $"(set plugins.\"{E2b}\".apiKey or environment variable {resolved.ApiKeyEnv})");
            return new NoopDisposable();
        }
        _ = new E2bRuntime(ctx, resolved);
        return new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal static class E2bConfig
{
    public static E2bRuntimeConfig From(object? config)
    {
        if (config is not IReadOnlyDictionary<string, object?> dict)
            return new E2bRuntimeConfig();
        return new E2bRuntimeConfig
        {
            ApiKey = dict.GetValueOrDefault("apiKey") as string,
            ApiKeyEnv = dict.GetValueOrDefault("apiKeyEnv") as string ?? E2bRuntimeConfig.DefaultApiKeyEnv,
            BaseUrl = dict.GetValueOrDefault("baseUrl") as string ?? E2bRuntimeConfig.DefaultBaseUrl,
            Template = dict.GetValueOrDefault("template") as string ?? E2bRuntimeConfig.DefaultTemplate,
            TimeoutMs = LongOf(dict, "timeoutMs") ?? E2bRuntimeConfig.DefaultTimeoutMs,
        };
    }

    private static long? LongOf(IReadOnlyDictionary<string, object?> dict, string key)
        => dict.GetValueOrDefault(key) switch
        {
            long value => value,
            int value => value,
            double value when double.IsFinite(value) && value % 1 == 0 => (long)value,
            _ => null,
        };
}
