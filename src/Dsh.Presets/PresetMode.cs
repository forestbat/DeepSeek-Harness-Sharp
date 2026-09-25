using Dsh.Core;
using Dsh.Plugins;

namespace Dsh.Presets;

/** 交互 preset id,与上游四模式对齐;持久化为字符串,新增 preset 不改事件格式。 */
public static class InteractionPreset
{
    public const string Standard = "standard";
    public const string Minimal = "minimal";
    public const string Ptc = "ptc";
    public const string Creative = "creative";

    public static readonly IReadOnlyList<string> All = [Standard, Minimal, Ptc, Creative];

    public static bool IsKnown(string? value)
        => value is not null && All.Contains(value, StringComparer.Ordinal);
}

public static class PresetEvents
{
    public const string Mode = "preset/mode";
}

public sealed record PresetModePayload(string Preset) : SessionEventPayload
{
    public override string Type => PresetEvents.Mode;

    public static void RegisterCodec()
        => SessionEventCodec.Register<PresetModePayload>(PresetEvents.Mode);
}

internal static class PresetCodecRegistration
{
    // 启动时由生成目录显式调用：preset 事件可能在 PresetController 构造之前被日志读写。
    [DshPluginInitializer]
    internal static void Register() => PresetModePayload.RegisterCodec();
}
