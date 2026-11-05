namespace Dsh.Compaction;

public record CompactionPolicyConfig
{
    public double? ThresholdRatio { get; init; }

    /**
     * 压缩后原样保留的尾部预算(token): 0 表示只保留当前未消费的一步(等价 fresh start)。
     * 实际取值还会被触发阈值的一半夹紧, 见 CompactionConfigResolver.EffectiveRetainTokens。
     */
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

public sealed record ResolvedConfig(
    double ThresholdRatio,
    int RetainTokens,
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
    int RetainTokens,
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

    /**
     * 尾部保留预算默认值: 与 Kilo Code 的 compaction.preserve_recent_tokens 上限(8,000)一致。
     * 旧实现是"窗口的 16%", 在 1M 窗口下会留 160K, 压缩后残留被尾部撑到 120K 以上。
     */
    public const int DefaultRetainTokens = 8_000;
    public const int DefaultTailTurns = 2;
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
        var retainTokens = config.RetainTokens ?? DefaultRetainTokens;
        var modelPolicies = ResolveModelPolicies(config.ModelPolicies);

        return new ResolvedConfig(
            thresholdRatio,
            retainTokens,
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
            policyOverride?.RetainTokens ?? config.RetainTokens,
            policyOverride?.TailTurns ?? config.TailTurns,
            policyOverride?.SummarizationProvider ?? config.SummarizationProvider,
            policyOverride?.SummarizationModel ?? config.SummarizationModel,
            policyOverride?.MaxTokens ?? config.MaxTokens,
            policyOverride?.CompactionRetries ?? config.CompactionRetries,
            policyOverride?.MaxOverflowRetries ?? config.MaxOverflowRetries,
            policyOverride?.DegradedRetainTurns ?? config.DegradedRetainTurns);
    }

    /**
     * 尾部保留预算的实际值: 配置值是上限, 再按触发阈值的一半夹紧。
     * 留超过一半会让压缩后仍停在触发线附近甚至以上 —— 选区为空, 压缩等于没做, 之后每一步都白跑一次。
     */
    public static int EffectiveRetainTokens(int configured, double thresholdRatio, int? contextWindow)
    {
        if (contextWindow is not { } window || window <= 0)
            return configured;
        var thresholdTokens = (int)Math.Floor(window * thresholdRatio);
        return Math.Clamp(configured, 0, Math.Max(0, thresholdTokens / 2));
    }

    public static ResolvedCompactSpec ResolveCompactSpec(ResolvedTargetPolicy policy, int contextWindow)
    {
        var targetKey = $"{policy.Provider}/{policy.Model}";
        if (contextWindow <= 0)
            throw new TargetPressureConfigError(targetKey, $"BasicCompactionConfig: contextWindow ({contextWindow}) must be a positive integer");
        var thresholdTokens = (int)Math.Floor(contextWindow * policy.ThresholdRatio);
        var retainTokens = EffectiveRetainTokens(policy.RetainTokens, policy.ThresholdRatio, contextWindow);
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
        if (config.RetainTokens is { } retainTokens)
            AssertNonNegative($"{name}.retainTokens", retainTokens);
        if (config.TailTurns is { } tailTurns)
            AssertPositive($"{name}.tailTurns", tailTurns);
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
