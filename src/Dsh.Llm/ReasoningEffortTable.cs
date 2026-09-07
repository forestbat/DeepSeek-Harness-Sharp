using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm;

public sealed record ReasoningEffortEntry(string Id, string Name, string? Description);

public sealed record ReasoningEffortProviderTable(
    string Provider,
    string? ModelPrefix,
    IReadOnlyList<ReasoningEffortEntry> Efforts,
    string? Default);

public sealed class ReasoningEffortTable
{
    private static readonly IReadOnlyList<ReasoningEffortProviderTable> Builtin =
    [
        new(
            "deepseek-official",
            null,
            [
                new("off", "Off", "Use for simple tasks that do not need reasoning."),
                new("low", "Low", "Prefer for routine or latency-sensitive tasks."),
                new("high", "High", "The default balance for most tasks."),
                new("max", "Max", "Reserve for the hardest quality-first tasks."),
            ],
            "high"),
        new(
            "openai-compatible",
            null,
            [
                new("off", "Off", "Use for simple tasks that do not need reasoning."),
                new("low", "Low", "Prefer for routine or latency-sensitive tasks."),
                new("high", "High", "The default balance for most tasks."),
                new("max", "Max", "Reserve for the hardest quality-first tasks."),
            ],
            "high"),
        new(
            "anthropic",
            null,
            [
                new("none", "None", "Do not use extended thinking."),
                new("low", "Low", "Use extended thinking with low token budget."),
                new("high", "High", "Use extended thinking with high token budget."),
            ],
            "high"),
    ];

    private readonly IReadOnlyList<ReasoningEffortProviderTable> _tables;

    public ReasoningEffortTable(IReadOnlyList<ReasoningEffortProviderTable> tables)
    {
        _tables = tables;
    }

    public static ReasoningEffortTable Load(string? overridePath = null)
    {
        if (overridePath is not null && File.Exists(overridePath))
        {
            var overrides = JsonSerializer.Deserialize<List<ReasoningEffortProviderTable>>(File.ReadAllText(overridePath));
            if (overrides is { Count: > 0 })
                return new ReasoningEffortTable(overrides);
        }
        return new ReasoningEffortTable(Builtin);
    }

    public LlmModelReasoningInfo? Resolve(string provider, string model)
    {
        var table = _tables.FirstOrDefault(entry =>
            entry.Provider == provider
            && (entry.ModelPrefix is null || model.StartsWith(entry.ModelPrefix, StringComparison.OrdinalIgnoreCase)));
        if (table is null)
            return null;
        var efforts = table.Efforts
            .Select(effort => new LlmReasoningEffortInfo(ReasoningEffortId.Create(effort.Id), effort.Name, effort.Description))
            .ToList();
        var defaultEffort = table.Default is null ? (ReasoningEffortId?)null : ReasoningEffortId.Create(table.Default);
        return new LlmModelReasoningInfo(efforts, defaultEffort);
    }
}
