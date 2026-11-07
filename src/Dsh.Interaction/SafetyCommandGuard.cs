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
    public static IDisposable Register(Context ctx, Func<SafetySettings?> readSafety)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        return tools.Guard(exec => ShellCommandPattern.MatchingRule(readSafety()?.Blacklist, exec.Name, exec.Arguments) is { } rule
            ? $"blocked by safety.blacklist rule \"{rule}\"" : null);
    }

    public static IDisposable RegisterApprovals(Context ctx, Func<SafetySettings?> readSafety)
    {
        var remove = ctx.OnWaterfall<ApprovalRequestNotification>((notification, next) =>
        {
            var request = notification.Request;
            using var arguments = JsonDocument.Parse(request.Arguments ?? "{}");
            var ruleArguments = request.RuleArguments ?? arguments.RootElement;
            var safety = readSafety();
            return ShellCommandPattern.MatchingRule(safety?.Blacklist, request.ToolName, ruleArguments) is null
                && (safety?.AutoApprove == true || ShellCommandPattern.MatchingRule(safety?.Whitelist, request.ToolName, ruleArguments) is not null)
                ? ValueTask.FromResult<object?>(ApprovalOutcome.AllowedOnce)
                : next();
        }, new EventOptions { Global = true });
        return new DisposeAction(() => remove());
    }
}
