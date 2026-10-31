using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.ScriptHost;

namespace Dsh.Ptc;

/** Executes code through the shared script host and the normal tool dispatcher. */
public sealed class ToolRunCode
{
    public const string ToolName = "run_code";
    public const long DefaultTimeoutMs = 120_000;
    public const long MaxTimeoutMs = 600_000;
    private const string Description = """
        Run a C# program through the listed tool SDK, returning printed output and a JSON value.
        Image outputs from tool calls are delivered separately for you to inspect.
        """;

    private readonly ToolRuntime _tools;
    private readonly SubprocessService _subprocess;
    private readonly string _cwd;

    private ToolRunCode(Context ctx, string cwd)
    {
        _tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("run_code requires the tools service");
        _subprocess = ctx.Get<SubprocessService>(SubprocessService.ServiceName)
            ?? throw new InvalidOperationException("run_code requires the subprocess service");
        _cwd = cwd;
    }

    public static IDisposable Register(Context ctx, string cwd, bool debugBatching)
    {
        var tool = new ToolRunCode(ctx, cwd);
        return tool._tools.Register(tool.BuildRunCodeDefinition(debugBatching));
    }

    private ToolDefinition BuildRunCodeDefinition(bool debugBatching) => new()
    {
        Name = ToolName,
        Description = debugBatching ? $"{Description}\n{PtcSdkRenderer.BatchingInstructions}" : Description,
        Parameters = BuildParameters(),
        Output = new ToolOutputDefinition(BuildOutputSchema(), RenderRunCode),
        Execute = ExecuteRunCodeAsync,
        IsConcurrencySafe = _ => false,
    };

    private static JsonObject BuildParameters() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["code"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The C# program statement body to execute.",
            },
            ["description"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Short description of what the program does.",
            },
            ["timeoutMs"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = $"Wall-clock timeout in milliseconds (default {DefaultTimeoutMs}, max {MaxTimeoutMs}).",
            },
        },
        ["required"] = new JsonArray(JsonValue.Create("code"), JsonValue.Create("description")),
        ["additionalProperties"] = false,
    };

    private static JsonObject BuildOutputSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["logs"] = new JsonObject { ["type"] = "string" },
            ["result"] = new JsonObject(),
        },
        ["required"] = new JsonArray(JsonValue.Create("logs"), JsonValue.Create("result")),
        ["additionalProperties"] = false,
    };

    private async Task<object?> ExecuteRunCodeAsync(JsonElement arguments, ToolRunContext exec)
    {
        var code = arguments.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(code))
            throw new PtcRunException(PtcFailureKinds.Exception, "run_code requires a non-empty \"code\" string");
        var timeoutMs = ResolveTimeout(arguments);
        var bindings = _tools.Schemas(exec.Agent?.ScopeKey)
            .Select(schema => schema.Name)
            .Where(name => name != ToolName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var dispatcher = new PtcSubDispatch(_tools, exec);
        var client = new PtcScriptHostClient(_subprocess, _cwd, bindings, dispatcher.DispatchAsync);
        var outcome = await client.RunAsync(code, timeoutMs, exec.Signal);
        if (!outcome.Ok)
            throw new PtcRunException(outcome.Kind ?? PtcFailureKinds.Exception, ComposeFailure(outcome));
        return new JsonObject { ["logs"] = outcome.Logs, ["result"] = outcome.Value?.DeepClone() };
    }

    private static long ResolveTimeout(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("timeoutMs", out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return DefaultTimeoutMs;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var requested))
            throw new PtcRunException(PtcFailureKinds.Exception, "\"timeoutMs\" must be a number");
        return Math.Clamp(requested, 1, MaxTimeoutMs);
    }

    private static string ComposeFailure(PtcRunOutcome outcome)
    {
        var builder = new StringBuilder();
        builder.Append('[').Append(outcome.Kind).Append("] ").Append(outcome.Message);
        if (!string.IsNullOrEmpty(outcome.Logs))
            builder.Append("\n[logs]\n").Append(outcome.Logs);
        return builder.ToString();
    }

    private static IReadOnlyList<ContentBlock> RenderRunCode(JsonElement arguments, JsonElement value)
    {
        var builder = new StringBuilder();
        if (value.TryGetProperty("logs", out var logs) && logs.GetString() is { Length: > 0 } logsText)
            builder.Append(logsText);
        if (value.TryGetProperty("result", out var result) && result.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(result.GetRawText());
        }
        return [new TextBlock(builder.Length == 0 ? "(no output)" : builder.ToString())];
    }
}

public sealed class PtcRunException(string kind, string message)
    : HarnessException(message, $"PTC_{kind.Replace('-', '_').ToUpperInvariant()}");
