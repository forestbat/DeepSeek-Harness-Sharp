using Dsh.Core;

namespace Dsh.Compaction;

/** settings.yaml 顶层 compaction_model 的解析: 供压缩摘要与首轮自动命名共用。 */
public static class CompactionModelSetting
{
    /** `provider/model` → (provider, model); 无斜杠视为仅 model 名, 由调用方配 provider。 */
    public static (string Provider, string Model)? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        var slash = trimmed.IndexOf('/');
        return slash <= 0 || slash == trimmed.Length - 1
            ? null
            : (trimmed[..slash], trimmed[(slash + 1)..]);
    }

    /** 取模型名部分(去掉可选的 provider 前缀)。 */
    public static string? ModelName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        var slash = trimmed.IndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }

    /** compaction_model ?? 会话模型: 完整 `provider/model` 直接用; 仅 model 名配会话的 provider。 */
    public static (string Provider, string Model)? Resolve(string? setting, Session session)
    {
        var sessionTarget = FromSession(session);
        if (Parse(setting) is { } full)
            return full;
        if (string.IsNullOrWhiteSpace(setting))
            return sessionTarget;
        var model = ModelName(setting)!;
        return sessionTarget is { } provider ? (provider.Provider, model) : null;
    }

    public static (string Provider, string Model)? FromSession(Session session)
        => session.RequestHeader()?.Config is { Provider.Length: > 0, Model.Length: > 0 } config
            ? (config.Provider, config.Model)
            : null;
}
