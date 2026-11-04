namespace Dsh.Compaction;

public record CompactionPolicyConfig
{
    public double? ThresholdRatio { get; init; }
    public double? RetainRatio { get; init; }
    public int? RetainTokens { get; init; }

    /** 保留的尾部"轮数": 数的是当前表面上最后 N 条真实用户消息(UserMessageSource), 不是请求数也不是 turn/start 次数。 */
    public int? TailTurns { get; init; }

    public string? SummarizationProvider { get; init; }
    public string? SummarizationModel { get; init; }
    public int? MaxTokens { get; init; }
    public int? CompactionRetries { get; init; }
    public int? MaxOverflowRetries { get; init; }

    /**
     * 摘要不可用(失败/比原文更大)时改为丢弃旧历史并保留的轮数上限(同样按表面上的用户消息数);
     * 0 表示不降级、直接失败。表面不足这么多条、或按此保留丢不掉足够多时, 会退到更少的轮数再试。
     */
    public int? DegradedRetainTurns { get; init; }
}

public sealed record ModelCompactPolicyConfig : CompactionPolicyConfig
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
}

[global::Dsh.Plugins.DshPluginSettings]
public sealed record BasicCompactionConfig : CompactionPolicyConfig
{
    public IReadOnlyList<ModelCompactPolicyConfig>? ModelPolicies { get; init; }
    public bool? Auto { get; init; }
}

public abstract record ResolvedRetention
{
    public sealed record Ratio(double RetainRatio) : ResolvedRetention;

    public sealed record Tokens(int RetainTokens) : ResolvedRetention;
}

public sealed record ResolvedConfig(
    double ThresholdRatio,
    ResolvedRetention Retention,
    int TailTurns,
    string SummarizationProvider,
    string SummarizationModel,
    int MaxTokens,
    int CompactionRetries,
    int MaxOverflowRetries,
    IReadOnlyList<ModelCompactPolicyConfig> ModelPolicies,
    bool Auto,
    int DegradedRetainTurns);

public sealed record ResolvedTargetPolicy(
    string Provider,
    string Model,
    double ThresholdRatio,
    ResolvedRetention Retention,
    int TailTurns,
    string SummarizationProvider,
    string SummarizationModel,
    int MaxTokens,
    int CompactionRetries,
    int MaxOverflowRetries,
    int DegradedRetainTurns);

public sealed record ResolvedCompactSpec(
    string Provider,
    string Model,
    int ContextWindow,
    double ThresholdRatio,
    int ThresholdTokens,
    int RetainTokens,
    int TailTurns,
    string SummarizationProvider,
    string SummarizationModel,
    int MaxTokens,
    int CompactionRetries,
    int MaxOverflowRetries,
    int DegradedRetainTurns);

public sealed class TargetPressureConfigError(string targetKey, string message) : Exception(message)
{
    public string TargetKey { get; } = targetKey;
}

public static class CompactionConfigResolver
{
    private const string PolicyKeySeparator = "\u001f";

    public const double DefaultThresholdRatio = 0.8;
    public const double DefaultRetainRatio = 0.16;
    public const int DefaultTailTurns = 2;
    public const int UnknownContextRetainTokens = 2_000;
    public const int DefaultMaxTokens = 8192;
    public const int DefaultCompactionRetries = 1;
    public const int DefaultMaxOverflowRetries = 1;

    /** 摘要不可用时保留的轮数: 6 轮原文足以让模型接上正在做的事, 又不需要任何摘要。 */
    public const int DefaultDegradedRetainTurns = 6;

    public static ResolvedConfig ResolveConfig(BasicCompactionConfig? config = null)
    {
        config ??= new BasicCompactionConfig();
        ValidatePolicy(config, "BasicCompactionConfig");

        var thresholdRatio = config.ThresholdRatio ?? DefaultThresholdRatio;
        var retention = ResolveRetention(config, new ResolvedRetention.Ratio(DefaultRetainRatio));
        ValidateRatioRetention(thresholdRatio, retention, "BasicCompactionConfig");
        var modelPolicies = ResolveModelPolicies(config.ModelPolicies);
        for (var index = 0; index < modelPolicies.Count; index++)
        {
            ValidateRatioRetention(
                modelPolicies[index].ThresholdRatio ?? thresholdRatio,
                ResolveRetention(modelPolicies[index], retention),
                $"BasicCompactionConfig: modelPolicies[{index}]");
        }

        return new ResolvedConfig(
            thresholdRatio,
            retention,
            config.TailTurns ?? DefaultTailTurns,
            config.SummarizationProvider ?? "",
            config.SummarizationModel ?? "",
            config.MaxTokens ?? DefaultMaxTokens,
            config.CompactionRetries ?? DefaultCompactionRetries,
            config.MaxOverflowRetries ?? DefaultMaxOverflowRetries,
            modelPolicies,
            config.Auto ?? true,
            config.DegradedRetainTurns ?? DefaultDegradedRetainTurns);
    }

    public static ResolvedTargetPolicy ResolveTargetPolicy(ResolvedConfig config, string provider, string model)
    {
        var policyOverride = config.ModelPolicies.FirstOrDefault(policy => policy.Provider == provider && policy.Model == model);
        return new ResolvedTargetPolicy(
            provider,
            model,
            policyOverride?.ThresholdRatio ?? config.ThresholdRatio,
            ResolveRetention(policyOverride, config.Retention),
            policyOverride?.TailTurns ?? config.TailTurns,
            policyOverride?.SummarizationProvider ?? config.SummarizationProvider,
            policyOverride?.SummarizationModel ?? config.SummarizationModel,
            policyOverride?.MaxTokens ?? config.MaxTokens,
            policyOverride?.CompactionRetries ?? config.CompactionRetries,
            policyOverride?.MaxOverflowRetries ?? config.MaxOverflowRetries,
            policyOverride?.DegradedRetainTurns ?? config.DegradedRetainTurns);
    }

    /** 尾部保留预算:配置为 token 时直接采用;为比例且已知上下文窗口时按比例换算;否则回退到保守常量。 */
    public static int ResolveRetainTokens(ResolvedRetention retention, int? contextWindow)
        => retention switch
        {
            ResolvedRetention.Tokens tokens => tokens.RetainTokens,
            ResolvedRetention.Ratio ratio when contextWindow is { } window => (int)Math.Floor(window * ratio.RetainRatio),
            _ => UnknownContextRetainTokens,
        };

    public static ResolvedCompactSpec ResolveCompactSpec(ResolvedTargetPolicy policy, int contextWindow)
    {
        var targetKey = $"{policy.Provider}/{policy.Model}";
        if (contextWindow <= 0)
            throw new TargetPressureConfigError(targetKey, $"BasicCompactionConfig: contextWindow ({contextWindow}) must be a positive integer");
        var thresholdTokens = (int)Math.Floor(contextWindow * policy.ThresholdRatio);
        var retainTokens = ResolveRetainTokens(policy.Retention, contextWindow);
        if (retainTokens >= thresholdTokens)
            throw new TargetPressureConfigError(
                targetKey,
                $"BasicCompactionConfig: {policy.Provider}/{policy.Model} retainTokens ({retainTokens}) must be less than threshold tokens {thresholdTokens}");
        return new ResolvedCompactSpec(
            policy.Provider,
            policy.Model,
            contextWindow,
            policy.ThresholdRatio,
            thresholdTokens,
            retainTokens,
            policy.TailTurns,
            policy.SummarizationProvider,
            policy.SummarizationModel,
            policy.MaxTokens,
            policy.CompactionRetries,
            policy.MaxOverflowRetries,
            policy.DegradedRetainTurns);
    }

    private static ResolvedRetention ResolveRetention(CompactionPolicyConfig? config, ResolvedRetention fallback)
    {
        if (config?.RetainTokens is { } retainTokens)
            return new ResolvedRetention.Tokens(retainTokens);
        if (config?.RetainRatio is { } retainRatio)
            return new ResolvedRetention.Ratio(retainRatio);
        return fallback;
    }

    private static void ValidateRatioRetention(double thresholdRatio, ResolvedRetention retention, string name)
    {
        if (retention is ResolvedRetention.Ratio { RetainRatio: var retainRatio } && retainRatio >= thresholdRatio)
            throw new ArgumentException($"{name}: retainRatio ({retainRatio}) must be less than the resolved thresholdRatio ({thresholdRatio})");
    }

    private static IReadOnlyList<ModelCompactPolicyConfig> ResolveModelPolicies(IReadOnlyList<ModelCompactPolicyConfig>? configured)
    {
        if (configured is null)
            return [];
        var seen = new HashSet<string>();
        var result = new List<ModelCompactPolicyConfig>();
        for (var index = 0; index < configured.Count; index++)
        {
            var source = configured[index];
            var name = $"BasicCompactionConfig: modelPolicies[{index}]";
            if (string.IsNullOrEmpty(source.Provider))
                throw new ArgumentException($"{name}.provider must be a non-empty string");
            if (string.IsNullOrEmpty(source.Model))
                throw new ArgumentException($"{name}.model must be a non-empty string");
            ValidatePolicy(source, name);
            var key = $"{source.Provider}{PolicyKeySeparator}{source.Model}";
            if (!seen.Add(key))
                throw new ArgumentException($"BasicCompactionConfig: duplicate model policy for {source.Provider}/{source.Model}");
            result.Add(source);
        }
        return result;
    }

    private static void ValidatePolicy(CompactionPolicyConfig config, string name)
    {
        if (config.ThresholdRatio is { } thresholdRatio)
            AssertRatio($"{name}.thresholdRatio", thresholdRatio);
        if (config.RetainRatio is { } retainRatio)
            AssertRatio($"{name}.retainRatio", retainRatio);
        if (config.RetainTokens is { } retainTokens)
            AssertNonNegative($"{name}.retainTokens", retainTokens);
        if (config.TailTurns is { } tailTurns)
            AssertPositive($"{name}.tailTurns", tailTurns);
        if (config.RetainRatio is not null && config.RetainTokens is not null)
            throw new ArgumentException($"{name}: retainRatio and retainTokens are mutually exclusive");
        if (config.MaxTokens is { } maxTokens)
            AssertPositive($"{name}.maxTokens", maxTokens);
        if (config.CompactionRetries is { } compactionRetries)
            AssertNonNegative($"{name}.compactionRetries", compactionRetries);
        if (config.MaxOverflowRetries is { } maxOverflowRetries)
            AssertNonNegative($"{name}.maxOverflowRetries", maxOverflowRetries);
        if (config.DegradedRetainTurns is { } degradedRetainTurns)
            AssertNonNegative($"{name}.degradedRetainTurns", degradedRetainTurns);

        var provider = config.SummarizationProvider;
        var model = config.SummarizationModel;
        if (provider is null && model is null)
            return;
        if (provider is null || model is null || string.IsNullOrEmpty(provider) != string.IsNullOrEmpty(model))
            throw new ArgumentException($"{name}: summarizationProvider and summarizationModel must be set together as an empty or non-empty pair");
    }

    private static void AssertPositive(string name, int value)
    {
        if (value <= 0)
            throw new ArgumentException($"{name} ({value}) must be a positive integer");
    }

    private static void AssertNonNegative(string name, int value)
    {
        if (value < 0)
            throw new ArgumentException($"{name} ({value}) must be a non-negative integer");
    }

    private static void AssertRatio(string name, double value)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 1)
            throw new ArgumentException($"{name} ({value}) must be a number in (0, 1]");
    }
}
