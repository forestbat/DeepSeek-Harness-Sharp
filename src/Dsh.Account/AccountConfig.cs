namespace Dsh.Account;

/** 账号功能配置: 默认走官方地址, 私有部署只经本库插件参数覆盖, 不写入源码仓库。 */
public sealed record AccountConfig
{
    public const string DefaultPlatformOrigin = "https://platform.deepseek.com";
    public const string DefaultInferenceOrigin = "https://api.deepseek.com";
    public const int DefaultRequestTimeoutMs = 30_000;
    public const int DefaultAttemptTimeoutMs = 600_000;
    public const int DefaultLogoutMaxRetries = 5;
    public const int DefaultLogoutRetryDelayMs = 1_000;

    public string PlatformOrigin { get; init; } = DefaultPlatformOrigin;

    public string InferenceOrigin { get; init; } = DefaultInferenceOrigin;

    public bool AllowLoopbackHttp { get; init; }

    public bool Enabled { get; init; }

    public int RequestTimeoutMs { get; init; } = DefaultRequestTimeoutMs;

    public int AttemptTimeoutMs { get; init; } = DefaultAttemptTimeoutMs;

    public int LogoutMaxRetries { get; init; } = DefaultLogoutMaxRetries;

    public int LogoutRetryDelayMs { get; init; } = DefaultLogoutRetryDelayMs;

    public string Locale { get; init; } = "zh_CN";

    public string ClientPlatform { get; init; } = "web";

    public static AccountConfig From(object? config)
    {
        var values = config as IReadOnlyDictionary<string, object?>;
        return new AccountConfig
        {
            PlatformOrigin = Text(values, "platformOrigin") ?? DefaultPlatformOrigin,
            InferenceOrigin = Text(values, "inferenceOrigin") ?? DefaultInferenceOrigin,
            AllowLoopbackHttp = Flag(values, "allowLoopbackHttp") ?? false,
            Enabled = Flag(values, "accountEnabled") ?? false,
            RequestTimeoutMs = Clamp(Int(values, "requestTimeoutMs") ?? DefaultRequestTimeoutMs, 1, 120_000),
            AttemptTimeoutMs = Clamp(Int(values, "attemptTimeoutMs") ?? DefaultAttemptTimeoutMs, 1, 3_600_000),
            LogoutMaxRetries = Clamp(Int(values, "logoutMaxRetries") ?? DefaultLogoutMaxRetries, 0, 5),
            LogoutRetryDelayMs = Clamp(Int(values, "logoutRetryDelayMs") ?? DefaultLogoutRetryDelayMs, 1, 60_000),
            Locale = string.Equals(Text(values, "locale"), "en_US", StringComparison.Ordinal) ? "en_US" : "zh_CN",
            ClientPlatform = Text(values, "clientPlatform") ?? "web",
        };
    }

    private static string? Text(IReadOnlyDictionary<string, object?>? values, string key)
        => values?.GetValueOrDefault(key) is string text && text.Length > 0 ? text : null;

    private static bool? Flag(IReadOnlyDictionary<string, object?>? values, string key)
        => values?.GetValueOrDefault(key) as bool?;

    private static int? Int(IReadOnlyDictionary<string, object?>? values, string key)
        => values?.GetValueOrDefault(key) switch
        {
            int value => value,
            long value => (int)value,
            double value => (int)value,
            _ => null,
        };

    private static int Clamp(int value, int minimum, int maximum) => Math.Clamp(value, minimum, maximum);
}
