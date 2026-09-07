using System.Text.Json;
using Cordis;
using Dsh.Core;

namespace Dsh.Boot;

public static class SafetyCommandGuard
{
    public static IDisposable Register(Context ctx, SafetySettings? safety)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        if (safety?.Blacklist is not { Count: > 0 })
            return new NoopDisposable();
        return tools.Guard(exec =>
        {
            var command = ExtractCommand(exec);
            var matched = safety.Blacklist
                .Where(rule => Matches(rule, exec.Name, command))
                .OrderBy(rule => rule.Length)
                .FirstOrDefault();
            return matched is null ? null : $"blocked by safety.blacklist rule \"{matched}\"";
        });
    }

    private static string? ExtractCommand(ToolExecution exec)
    {
        if (exec.Name is not ("bash" or "pwsh"))
            return null;
        if (exec.Arguments.ValueKind != JsonValueKind.Object)
            return null;
        if (exec.Arguments.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
            return command.GetString();
        return null;
    }

    private static bool Matches(string rule, string toolName, string? command)
    {
        if (rule.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(toolName, rule["tool:".Length..], StringComparison.Ordinal);
        if (rule.StartsWith("bash:", StringComparison.OrdinalIgnoreCase) && toolName == "bash" && command is not null)
            return command.Contains(rule["bash:".Length..], StringComparison.Ordinal);
        if (rule.StartsWith("pwsh:", StringComparison.OrdinalIgnoreCase) && toolName == "pwsh" && command is not null)
            return command.Contains(rule["pwsh:".Length..], StringComparison.Ordinal);
        return false;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}