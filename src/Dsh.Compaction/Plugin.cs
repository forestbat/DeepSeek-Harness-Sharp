using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Compaction.Plugin.TokenMeter)]
[assembly: DshPlugin(Dsh.Compaction.Plugin.CompactionToolResultPruner)]
[assembly: DshPlugin(Dsh.Compaction.Plugin.CompactionBasic)]
[assembly: DshPlugin(Dsh.Compaction.Plugin.CommandCompact)]
[assembly: DshPlugin(Dsh.Compaction.Plugin.ToolCompact)]

namespace Dsh.Compaction;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string TokenMeter = "@deepseek-ai/dsh-token-meter";
    internal const string CompactionToolResultPruner = "@deepseek-ai/dsh-compaction-tool-result-pruner";
    internal const string CompactionBasic = "@deepseek-ai/dsh-compaction-basic";
    internal const string CommandCompact = "@deepseek-ai/dsh-command-compact";
    internal const string ToolCompact = "@deepseek-ai/dsh-compaction-tool";

    public string[] Inject => packageName switch
    {
        TokenMeter => [],
        CompactionToolResultPruner => [Dsh.Compaction.TokenMeter.ServiceName],
        CompactionBasic => [LlmRuntime.ServiceName, Dsh.Compaction.TokenMeter.ServiceName, SessionStore.ServiceName],
        CommandCompact => [CommandsService.ServiceName, CompactionEngine.ServiceName],
        ToolCompact => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, CompactionEngine.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        TokenMeter => Dsh.Compaction.TokenMeter.Register(ctx),
        CompactionToolResultPruner => ToolResultPruner.Register(ctx, PruneConfigFrom(config)),
        CompactionBasic => RegisterBasicCompaction(ctx, config),
        CommandCompact => CompactCommand.Register(ctx),
        ToolCompact => CompactTool.Register(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

    /** 基础压缩同时挂载首轮自动命名(共用 compaction_model), 二者随插件一同释放。 */
    private static IDisposable RegisterBasicCompaction(Context ctx, object? config)
    {
        var engine = BasicCompactionEngine.Register(ctx, WithHarnessSummarization(ctx, BasicCompactionConfigFrom(config)));
        return ctx.GetProp("harnessOptions") is HarnessOptions options
            ? new Bundle(engine, new SessionAutoRename(ctx, options))
            : engine;
    }

    /** 插件未配 summarization 时, 回落到 settings.yaml 顶层 compaction_model。 */
    private static BasicCompactionConfig WithHarnessSummarization(Context ctx, BasicCompactionConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.SummarizationProvider) || !string.IsNullOrWhiteSpace(config.SummarizationModel))
            return config;
        if (ctx.GetProp("harnessOptions") is not HarnessOptions options)
            return config;
        return CompactionModelSetting.Parse(HarnessSettings.Load(options.Home).CompactionModel) is { } target
            ? config with { SummarizationProvider = target.Provider, SummarizationModel = target.Model }
            : config;
    }

    private static ToolResultPruneConfig PruneConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new ToolResultPruneConfig
        {
            ThresholdChars = IntOf(dict, "thresholdChars"),
            HeadChars = IntOf(dict, "headChars"),
            TailChars = IntOf(dict, "tailChars"),
        };
    }

    private static BasicCompactionConfig BasicCompactionConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new BasicCompactionConfig
        {
            ThresholdRatio = DoubleOf(dict, "thresholdRatio"),
            RetainRatio = DoubleOf(dict, "retainRatio"),
            RetainTokens = IntOf(dict, "retainTokens"),
            TailTurns = IntOf(dict, "tailTurns"),
            SummarizationProvider = dict?.GetValueOrDefault("summarizationProvider") as string,
            SummarizationModel = dict?.GetValueOrDefault("summarizationModel") as string,
            MaxTokens = IntOf(dict, "maxTokens"),
            CompactionRetries = IntOf(dict, "compactionRetries"),
            MaxOverflowRetries = IntOf(dict, "maxOverflowRetries"),
            ModelPolicies = ModelPoliciesFrom(dict?.GetValueOrDefault("modelPolicies")),
            Auto = dict?.GetValueOrDefault("auto") as bool?,
        };
    }

    private static IReadOnlyList<ModelCompactPolicyConfig>? ModelPoliciesFrom(object? value)
    {
        if (value is not IEnumerable<object?> items)
            return null;
        var policies = new List<ModelCompactPolicyConfig>();
        foreach (var item in items)
        {
            if (ConfigOf(item) is not { } dict)
                continue;
            policies.Add(new ModelCompactPolicyConfig
            {
                Provider = dict.GetValueOrDefault("provider") as string ?? "",
                Model = dict.GetValueOrDefault("model") as string ?? "",
                ThresholdRatio = DoubleOf(dict, "thresholdRatio"),
                RetainRatio = DoubleOf(dict, "retainRatio"),
                RetainTokens = IntOf(dict, "retainTokens"),
                TailTurns = IntOf(dict, "tailTurns"),
                SummarizationProvider = dict.GetValueOrDefault("summarizationProvider") as string,
                SummarizationModel = dict.GetValueOrDefault("summarizationModel") as string,
                MaxTokens = IntOf(dict, "maxTokens"),
                CompactionRetries = IntOf(dict, "compactionRetries"),
                MaxOverflowRetries = IntOf(dict, "maxOverflowRetries"),
            });
        }
        return policies;
    }

    private static int? IntOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => (int)value,
            int value => value,
            _ => null,
        };

    private static double? DoubleOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            double value => value,
            long value => value,
            int value => value,
            _ => null,
        };

    private sealed class Bundle(params IDisposable[] parts) : IDisposable
    {
        public void Dispose()
        {
            foreach (var part in parts)
                part.Dispose();
        }
    }
}