using System.Text.RegularExpressions;
using Dsh.Llm;

namespace Dsh.Core;

public static partial class PromptRender
{
    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex VariableName();

    public static bool IsValidVariableName(string name) => VariableName().IsMatch(name);

    [GeneratedRegex(@"^\{\{([^{}]*)\}\}")]
    private static partial Regex GroupAt();

    public static string RenderPrompt(PromptAssembly assembly)
        => string.Join("\n\n", assembly.Sections
            .Select(section => Interpolate(section.Name, section.Text, assembly.Variables, "section"))
            .Where(text => text.Length > 0));

    public static string RenderContextSnapshot(PromptAssembly assembly)
        => JoinContextSections(RenderContextSections(assembly));

    public static string JoinContextSections(IReadOnlyList<ContextSnapshotSection> sections)
    {
        var body = string.Join("\n\n", sections.Select(section => section.Text));
        return body.Length == 0
            ? ""
            : $"Current runtime context. This snapshot supersedes earlier runtime-context snapshots.\n\n{body}";
    }

    public static IReadOnlyList<ContextSnapshotSection> RenderContextSections(PromptAssembly assembly)
        => assembly.Contexts
            .Select(context => new ContextSnapshotSection(
                context.Name,
                Interpolate(context.Name, context.Text, assembly.Variables, "context")))
            .Where(section => section.Text.Length > 0)
            .ToList();

    private static string Interpolate(string name, string text, IReadOnlyDictionary<string, string?> variables, string kind)
    {
        var result = new System.Text.StringBuilder();
        var last = 0;
        for (var open = text.IndexOf("{{", last, StringComparison.Ordinal); open >= 0; open = text.IndexOf("{{", last, StringComparison.Ordinal))
        {
            var group = GroupAt().Match(text, open);
            if (!group.Success)
            {
                if (text.IndexOf("}}", open + 2, StringComparison.Ordinal) >= 0)
                {
                    throw new InvalidOperationException(
                        $"malformed prompt variable reference at \"{text.Substring(open, Math.Min(16, text.Length - open))}…\" in {kind} \"{name}\" (references are complete simple {{{{name}}}} groups)");
                }
                result.Append(text, last, open + 2 - last);
                last = open + 2;
                continue;
            }
            var variableName = group.Groups[1].Value;
            if (!VariableName().IsMatch(variableName))
            {
                throw new InvalidOperationException(
                    $"malformed prompt variable reference \"{{{{{variableName}}}}}\" in {kind} \"{name}\" (variable names match ^[a-z][a-z0-9_]*$)");
            }
            if (!variables.TryGetValue(variableName, out var value))
            {
                throw new InvalidOperationException(
                    $"unknown prompt variable \"{{{{{variableName}}}}}\" in {kind} \"{name}\"; registered variables: {(variables.Count > 0 ? string.Join(", ", variables.Keys) : "(none)")}");
            }
            if (value is null)
                throw new InvalidOperationException($"prompt variable \"{{{{{variableName}}}}}\" has no value for this assembly ({kind} \"{name}\")");
            result.Append(text, last, open - last).Append(value);
            last = open + group.Length;
        }
        return result.Append(text, last, text.Length - last).ToString();
    }
}
