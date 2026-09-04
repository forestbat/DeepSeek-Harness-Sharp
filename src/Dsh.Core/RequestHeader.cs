using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Core;

public static class RequestHeader
{
    public static EpochHeader Canonicalize(EpochHeader header)
    {
        var adapterDefaults = header.AdapterDefaults;
        return header with
        {
            AdapterDefaults = adapterDefaults is { ReasoningEffort: true } or { MaxTokens: true } ? adapterDefaults : null,
            System = string.IsNullOrEmpty(header.System) ? null : header.System,
            Tools = header.Tools is { Count: > 0 } ? header.Tools : null,
        };
    }

    public static bool Equals(EpochHeader a, EpochHeader b)
    {
        if (!a.Config.Equals(b.Config)
            || (a.AdapterDefaults?.ReasoningEffort ?? false) != (b.AdapterDefaults?.ReasoningEffort ?? false)
            || (a.AdapterDefaults?.MaxTokens ?? false) != (b.AdapterDefaults?.MaxTokens ?? false)
            || a.System != b.System)
            return false;
        var aTools = a.Tools ?? [];
        var bTools = b.Tools ?? [];
        return aTools.Count == bTools.Count
            && aTools.Zip(bTools).All(pair => JsonSerializer.Serialize(pair.First, DshJson.Options) == JsonSerializer.Serialize(pair.Second, DshJson.Options));
    }

    public static EpochHeader? Fold(IEnumerable<SessionEvent> events, EpochHeader? from = null)
    {
        var state = from;
        foreach (var sessionEvent in events)
        {
            if (sessionEvent.Data is RequestHeaderPayload header)
                state = Canonicalize(header.Header);
        }
        return state;
    }
}
