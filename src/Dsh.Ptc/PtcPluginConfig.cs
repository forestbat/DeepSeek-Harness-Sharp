using Dsh.Plugins;

namespace Dsh.Ptc;

/** Configures optional model guidance without changing tool dispatch. */
[DshPluginSettings]
public sealed record PtcPluginConfig
{
    /// <summary>Prefer batching independent, non-interactive tool operations in one run_code.</summary>
    [DshPluginSettings(Name = "debug_batching")]
    public bool DebugBatching { get; init; } = false;

    public static PtcPluginConfig Resolve(object? config)
        => config as PtcPluginConfig ?? new PtcPluginConfig
        {
            DebugBatching = config is IReadOnlyDictionary<string, object?> map
                && map.TryGetValue("debug_batching", out var value)
                && value is true,
        };
}
