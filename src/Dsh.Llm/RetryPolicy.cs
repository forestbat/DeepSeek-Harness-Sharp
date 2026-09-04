namespace Dsh.Llm;

public abstract record RetryPolicyConfig
{
    public sealed record Normal(
        int? MaxRetries = null,
        IReadOnlyList<string>? RetryableCodes = null,
        BackoffConfig? Backoff = null) : RetryPolicyConfig;

    public sealed record Always(BackoffConfig? Backoff = null) : RetryPolicyConfig;
}

public sealed record BackoffConfig(double? InitialDelayMs = null, double? MaxDelayMs = null, double? JitterRatio = null);

public abstract record ResolvedRetryPolicy
{
    public const long MaxTimerDelayMs = 2_147_483_647;

    public const int DefaultMaxRetries = 5;
    public const double DefaultInitialDelayMs = 500;
    public const double DefaultMaxDelayMs = 10_000;
    public const double DefaultJitterRatio = 0.1;

    public static readonly IReadOnlyList<string> DefaultRetryableCodes =
    [
        LlmFailureCodes.EmptyResponse,
        LlmFailureCodes.RateLimit,
        LlmFailureCodes.Server,
        LlmFailureCodes.Timeout,
        LlmFailureCodes.Transport,
    ];

    public required double InitialDelayMs { get; init; }
    public required double MaxDelayMs { get; init; }
    public required double JitterRatio { get; init; }

    public sealed record Normal(int MaxRetries, IReadOnlyList<string> RetryableCodes) : ResolvedRetryPolicy;

    public sealed record Always : ResolvedRetryPolicy;

    public static ResolvedRetryPolicy Resolve(RetryPolicyConfig? config, string path)
    {
        if (config is null)
        {
            var backoff = ResolveBackoff(null, $"{path}.backoff");
            return new Normal(DefaultMaxRetries, DefaultRetryableCodes)
            {
                InitialDelayMs = backoff.InitialDelayMs,
                MaxDelayMs = backoff.MaxDelayMs,
                JitterRatio = backoff.JitterRatio,
            };
        }
        return config switch
        {
            RetryPolicyConfig.Normal normal => ResolveNormal(normal, path),
            RetryPolicyConfig.Always always => ResolveAlways(always, path),
            _ => throw new ArgumentException($"{path}.mode must be \"normal\" or \"always\""),
        };
    }

    private static ResolvedRetryPolicy ResolveNormal(RetryPolicyConfig.Normal config, string path)
    {
        var maxRetries = config.MaxRetries ?? DefaultMaxRetries;
        var retryableCodes = config.RetryableCodes ?? DefaultRetryableCodes;
        if (maxRetries < 0)
            throw new ArgumentException($"{path}.maxRetries must be a non-negative integer");
        if (retryableCodes.Count == 0)
            throw new ArgumentException($"{path}.retryableCodes must not be empty");
        if (retryableCodes.Any(string.IsNullOrEmpty))
            throw new ArgumentException($"{path}.retryableCodes must contain only non-empty strings");
        if (retryableCodes.Distinct().Count() != retryableCodes.Count)
            throw new ArgumentException($"{path}.retryableCodes must not contain duplicates");
        var backoff = ResolveBackoff(config.Backoff, $"{path}.backoff");
        return new Normal(maxRetries, [..retryableCodes])
        {
            InitialDelayMs = backoff.InitialDelayMs,
            MaxDelayMs = backoff.MaxDelayMs,
            JitterRatio = backoff.JitterRatio,
        };
    }

    private static ResolvedRetryPolicy ResolveAlways(RetryPolicyConfig.Always config, string path)
    {
        var backoff = ResolveBackoff(config.Backoff, $"{path}.backoff");
        return new Always
        {
            InitialDelayMs = backoff.InitialDelayMs,
            MaxDelayMs = backoff.MaxDelayMs,
            JitterRatio = backoff.JitterRatio,
        };
    }

    private static (double InitialDelayMs, double MaxDelayMs, double JitterRatio) ResolveBackoff(BackoffConfig? config, string path)
    {
        var initialDelayMs = config?.InitialDelayMs ?? DefaultInitialDelayMs;
        var maxDelayMs = config?.MaxDelayMs ?? DefaultMaxDelayMs;
        var jitterRatio = config?.JitterRatio ?? DefaultJitterRatio;
        if (!double.IsFinite(initialDelayMs) || initialDelayMs <= 0 || initialDelayMs > MaxTimerDelayMs)
            throw new ArgumentException($"{path}.initialDelayMs must be a positive finite number no greater than {MaxTimerDelayMs}");
        if (!double.IsFinite(maxDelayMs) || maxDelayMs <= 0 || maxDelayMs > MaxTimerDelayMs)
            throw new ArgumentException($"{path}.maxDelayMs must be a positive finite number no greater than {MaxTimerDelayMs}");
        if (initialDelayMs > maxDelayMs)
            throw new ArgumentException($"{path}.initialDelayMs must be less than or equal to maxDelayMs");
        if (!double.IsFinite(jitterRatio) || jitterRatio < 0 || jitterRatio > 1)
            throw new ArgumentException($"{path}.jitterRatio must be between 0 and 1");
        return (initialDelayMs, maxDelayMs, jitterRatio);
    }
}
