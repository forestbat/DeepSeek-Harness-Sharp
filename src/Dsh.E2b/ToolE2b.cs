using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.E2b;

/** e2b_run:在临时云沙盒里执行一条 shell 命令,返回 stdout/stderr/退出码。 */
public static class ToolE2bTool
{
    public const string ToolName = "e2b_run";

    private const string SectionText =
        "Use the e2b_run tool to execute a shell command inside an isolated cloud sandbox (E2B). "
        + "Each call gets a fresh sandbox that is destroyed right after the command finishes; "
        + "chain steps with `&&` when state matters. Prefer this over local bash for untrusted or heavyweight work.";

    public static IDisposable Apply(Context ctx, object? config = null)
    {
        var timeoutMs = config is IReadOnlyDictionary<string, object?> dict
            && dict.GetValueOrDefault("timeoutMs") is long timeout
                ? timeout
                : E2bRuntimeConfig.DefaultTimeoutMs;
        return Register(ctx, timeoutMs);
    }

    public static IDisposable Register(Context ctx, long timeoutMs)
    {
        var runtime = ctx.Get<E2bRuntime>(E2bRuntime.ServiceName)
            ?? throw new InvalidOperationException("e2b service is not registered");
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:e2b_run", SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Run a shell command in an ephemeral E2B cloud sandbox; returns stdout, stderr and exit code.",
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["command"],
                  "properties": {
                    "command": { "type": "string", "description": "Shell command to run (use && to chain steps)." },
                    "template": { "type": "string", "description": "Optional sandbox template name; defaults to the configured template." }
                  }
                }
                """)!.AsObject(),
            TimeoutMs = timeoutMs,
            Output = new ToolOutputDefinition(
                JsonNode.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["sandboxId", "stdout", "stderr", "exitCode", "durationMs"],
                      "properties": {
                        "sandboxId": { "type": "string" },
                        "stdout": { "type": "string" },
                        "stderr": { "type": "string" },
                        "exitCode": { "type": "integer" },
                        "durationMs": { "type": "integer" }
                      }
                    }
                    """)!.AsObject(),
                (_, value) =>
                [
                    new TextBlock(
                        $"exitCode={value.GetProperty("exitCode").GetInt32()} "
                        + $"stdout={value.GetProperty("stdout").GetString()?.Length ?? 0}B "
                        + $"stderr={value.GetProperty("stderr").GetString()?.Length ?? 0}B"),
                ]),
            Execute = (args, exec) => Execute(args, exec, runtime),
        });
        return new Registration(registration, section);
    }

    private static async Task<object?> Execute(JsonElement args, ToolRunContext exec, E2bRuntime runtime)
    {
        var command = args.TryGetProperty("command", out var commandElement) ? commandElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("e2b_run requires a non-empty command");
        var template = args.TryGetProperty("template", out var templateElement) ? templateElement.GetString() : null;
        return await runtime.RunAsync(command, template, exec.Signal);
    }

    private sealed class Registration(IDisposable registration, IDisposable section) : IDisposable
    {
        public void Dispose()
        {
            section.Dispose();
            registration.Dispose();
        }
    }
}
