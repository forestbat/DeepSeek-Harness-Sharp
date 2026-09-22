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
    private static readonly IReadOnlyList<ReasoningEffortEntry> StandardEfforts =
    [
        new("off", "Off", "Use for simple tasks that do not need reasoning."),
        new("low", "Low", "Prefer for routine or latency-sensitive tasks."),
        new("high", "High", "The default balance for most tasks."),
        new("max", "Max", "Reserve for the hardest quality-first tasks."),
    ];

    /** provider 没有专属表但 settings 标记 reasoning: true 时使用的通用强度集合。 */
    public static LlmModelReasoningInfo DefaultReasoning { get; } = Materialize(StandardEfforts, "high");

    private static readonly IReadOnlyList<ReasoningEffortProviderTable> Builtin =
    [
        new(
            "deepseek-official",
            null,
            StandardEfforts,
            "high"),
        new(
            "openai-compatible",
            null,
            StandardEfforts,
            "high"),
        new(
            "anthropic",
            null,
            [
                new("low", "Low", "Prefer for routine or latency-sensitive tasks."),
                new("medium", "Medium", "The balanced effort for most tasks."),
                new("high", "High", "Reserve for the hardest quality-first tasks."),
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
            var overrides = DshJson.Deserialize<List<ReasoningEffortProviderTable>>(File.ReadAllText(overridePath));
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
        return table is null ? null : Materialize(table.Efforts, table.Default);
    }

    private static LlmModelReasoningInfo Materialize(IReadOnlyList<ReasoningEffortEntry> efforts, string? defaultEffort)
    {
        var infos = efforts
            .Select(effort => new LlmReasoningEffortInfo(ReasoningEffortId.Create(effort.Id), effort.Name, effort.Description))
            .ToList();
        var defaultId = defaultEffort is null ? (ReasoningEffortId?)null : ReasoningEffortId.Create(defaultEffort);
        return new LlmModelReasoningInfo(infos, defaultId);
    }
}
