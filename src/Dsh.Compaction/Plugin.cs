using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Compaction.Plugin.TokenMeter)]
[assembly: DshPlugin(Dsh.Compaction.Plugin.CompactionToolResultPruner)]
[assembly: DshPlugin(Dsh.Compaction.Plugin.CompactionBasic)]
[assembly: DshPlugin(Dsh.Compaction.Plugin.CommandCompact)]

namespace Dsh.Compaction;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string TokenMeter = "@deepseek-ai/dsh-token-meter";
    internal const string CompactionToolResultPruner = "@deepseek-ai/dsh-compaction-tool-result-pruner";
    internal const string CompactionBasic = "@deepseek-ai/dsh-compaction-basic";
    internal const string CommandCompact = "@deepseek-ai/dsh-command-compact";

    public string[] Inject => packageName switch
    {
        TokenMeter => [],
        CompactionToolResultPruner => [Dsh.Compaction.TokenMeter.ServiceName],
        CompactionBasic => [LlmRuntime.ServiceName, Dsh.Compaction.TokenMeter.ServiceName, SessionStore.ServiceName],
        CommandCompact => [CommandsService.ServiceName, CompactionEngine.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        TokenMeter => Dsh.Compaction.TokenMeter.Register(ctx),
        CompactionToolResultPruner => ToolResultPruner.Register(ctx, PruneConfigFrom(config)),
        CompactionBasic => BasicCompactionEngine.Register(ctx, BasicCompactionConfigFrom(config)),
        CommandCompact => CompactCommand.Register(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

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
}