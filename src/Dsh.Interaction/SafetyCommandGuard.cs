using System.Text.Json;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Boot;
using Dsh.Core;

namespace Dsh.Interaction;

/**
 * Pre-execution guard for safety.blacklist.
 * Shell rules are matched against the tokenized simple commands of a shell tool call; this is a best-effort tripwire, not a sandbox.
 */
public static class SafetyCommandGuard
{
    private const string ToolPrefix = "tool:";

    public static IDisposable Register(Context ctx, Func<SafetySettings?> readSafety)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        return tools.Guard(exec => MatchingRule(readSafety()?.Blacklist, exec.Name, exec.Arguments) is { } rule
            ? $"blocked by safety.blacklist rule \"{rule}\"" : null);
    }

    /** Whitelist and blacklist deliberately share the current tool/command grammar. */
    public static string? MatchingRule(IReadOnlyList<string>? rules, string toolName, JsonElement arguments)
    {
        if (rules is null || rules.Count == 0)
            return null;
        var command = ShellToolDialects.ByToolName.TryGetValue(toolName, out var dialect)
            && arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("command", out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var segments = command is null ? [] : ShellCommandSegments.Split(command, dialect);
        return rules.OrderBy(rule => rule.Length).Select(SafetyRule.Parse)
            .FirstOrDefault(rule => rule.Matches(toolName, segments))?.Text;
    }

    public static IDisposable RegisterApprovals(Context ctx, Func<SafetySettings?> readSafety)
    {
        var remove = ctx.OnWaterfall<ApprovalRequestNotification>((notification, next) =>
        {
            var request = notification.Request;
            using var arguments = JsonDocument.Parse(request.Arguments ?? "{}");
            var ruleArguments = request.RuleArguments ?? arguments.RootElement;
            var safety = readSafety();
            return MatchingRule(safety?.Blacklist, request.ToolName, ruleArguments) is null
                && (safety?.AutoApprove == true || MatchingRule(safety?.Whitelist, request.ToolName, ruleArguments) is not null)
                ? ValueTask.FromResult<object?>(ApprovalOutcome.AllowedOnce)
                : next();
        }, new EventOptions { Global = true });
        return new DisposeAction(() => remove());
    }


    /**
     * "tool:<name>" blocks a tool by name.
     * "<shell tool>:<pattern>" (e.g. "bash:", "pwsh:") blocks matching commands in every shell tool of that tool's dialect, so a POSIX rule also covers bash_persistent and wsl.
     */
    private sealed record SafetyRule(string Text, string? ToolName, ShellDialect? Dialect, ShellCommandPattern? Pattern)
    {
        public static SafetyRule Parse(string text)
        {
            if (text.StartsWith(ToolPrefix, StringComparison.OrdinalIgnoreCase))
                return new SafetyRule(text, text[ToolPrefix.Length..], null, null);
            foreach (var (tool, dialect) in ShellToolDialects.ByToolName)
            {
                if (text.StartsWith($"{tool}:", StringComparison.OrdinalIgnoreCase))
                    return new SafetyRule(text, null, dialect, ShellCommandPattern.Parse(text[(tool.Length + 1)..], dialect));
            }
            return new SafetyRule(text, null, null, null);
        }

        public bool Matches(string toolName, IReadOnlyList<IReadOnlyList<string>> segments)
            => Pattern is null
                ? ToolName == toolName
                : ShellToolDialects.ByToolName.TryGetValue(toolName, out var dialect) && dialect == Dialect && Pattern.Matches(segments);
    }

}
