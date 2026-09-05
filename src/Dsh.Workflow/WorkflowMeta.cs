using Dsh.Llm;

namespace Dsh.Workflow;

public static class WorkflowMetaValidator
{
    public static WorkflowMeta ValidateMeta(object? value)
    {
        if (value is WorkflowMeta meta)
            return meta;

        var violations = new List<string>();
        if (value is not IDictionary<string, object?> record)
            return Fail(["meta must be an object"]);

        var known = new HashSet<string>(StringComparer.Ordinal) { "name", "description", "whenToUse", "phases" };
        foreach (var key in record.Keys)
        {
            if (!known.Contains(key))
                violations.Add($"meta.{key} is not a recognized field (name/description/whenToUse/phases)");
        }

        var name = ValueOf(record, "name") as string ?? "";
        var description = ValueOf(record, "description") as string ?? "";
        if (name.Length == 0)
            violations.Add("meta.name must be a non-empty string");
        if (description.Length == 0)
            violations.Add("meta.description must be a non-empty string");
        if (ValueOf(record, "whenToUse") is { } whenToUse && whenToUse is not string)
            violations.Add("meta.whenToUse must be a string");

        var phases = new List<WorkflowPhase>();
        if (ValueOf(record, "phases") is { } rawPhases)
        {
            if (rawPhases is not IEnumerable<object?> phaseItems)
            {
                violations.Add("meta.phases must be an array");
            }
            else
            {
                var index = 0;
                foreach (var rawPhase in phaseItems)
                {
                    if (rawPhase is not IDictionary<string, object?> phase)
                    {
                        violations.Add($"meta.phases[{index}] must be an object");
                        index++;
                        continue;
                    }

                    foreach (var key in phase.Keys)
                    {
                        if (key is not ("title" or "detail" or "provider" or "model"))
                            violations.Add($"meta.phases[{index}].{key} is not a recognized field");
                    }

                    var phaseTitle = ValueOf(phase, "title") as string ?? "";
                    if (phaseTitle.Length == 0)
                        violations.Add($"meta.phases[{index}].title must be a non-empty string");
                    if (ValueOf(phase, "detail") is { } detail && detail is not string)
                        violations.Add($"meta.phases[{index}].detail must be a string");
                    if (ValueOf(phase, "provider") is { } provider && provider is not string)
                        violations.Add($"meta.phases[{index}].provider must be a string");
                    if (ValueOf(phase, "model") is { } model && model is not string)
                        violations.Add($"meta.phases[{index}].model must be a string");

                    if (violations.Count == 0)
                    {
                        phases.Add(new WorkflowPhase(
                            phaseTitle,
                            ValueOf(phase, "detail") as string,
                            ValueOf(phase, "provider") as string,
                            ValueOf(phase, "model") as string));
                    }

                    index++;
                }
            }
        }

        if (violations.Count > 0)
            return Fail(violations);

        return new WorkflowMeta(
            name,
            description,
            ValueOf(record, "whenToUse") as string,
            ValueOf(record, "phases") is null ? null : phases);

        static WorkflowMeta Fail(IReadOnlyList<string> list)
            => throw new WorkflowError($"invalid meta: {string.Join("; ", list)}", WorkflowErrorCodes.MetaInvalid);
    }

    private static object? ValueOf(IDictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var value) ? value : null;
}