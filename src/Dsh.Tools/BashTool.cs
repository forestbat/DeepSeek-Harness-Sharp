using System.Text.Json;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tools;

public sealed record BashToolConfig
{
    public string? Cwd { get; init; }
    public long TimeoutMs { get; init; } = 120_000;
    public long MaxTimeoutMs { get; init; } = 600_000;
    public int MaxOutputBytes { get; init; } = 64_000;
    public int MaxSpillBytes { get; init; } = 64 * 1024 * 1024;
}

public sealed record BashStreamOutput(string Text, bool Truncated, string? SpillPath);

public sealed record BashRunValue(
    string Kind,
    int? ExitCode,
    string? Signal,
    bool TimedOut,
    bool Aborted,
    long TimeoutMs,
    BashStreamOutput Stdout,
    BashStreamOutput Stderr);

public static class BashTool
{
    public const string ToolName = "bash";

    private const string SectionText = "Check the [exit code: N] marker on every bash result; investigate failures before moving on.";

    private const string Description =
        "Execute a bash command (`bash -c`) and return its stdout/stderr. "
        + "Each call runs in a fresh shell: no state (cwd, variables, functions) persists between calls — "
        + "pass `workdir` instead of using `cd`. Non-zero exits are reported as `[exit code: N]`. "
        + "Current harness environment facts are exposed through managed `$DSH_*` variables; inspect them when needed. "
        + "Commands may run under a file sandbox; a blocked file operation is reported as `[sandbox: file access denied under <mode> mode]` — a policy denial, not a bug in the command; do not retry another way. "
        + "Long output is truncated to its tail; the full output is saved to a file whose path is reported when available. "
        + "Background execution is not available; long-running commands must finish within the timeout.";

    private static readonly IReadOnlyDictionary<string, string?> EnvOverrides = new Dictionary<string, string?>
    {
        ["NO_COLOR"] = "1",
        ["TERM"] = "dumb",
        ["PAGER"] = "cat",
        ["GIT_PAGER"] = "cat",
    };

    public static IDisposable Register(Context ctx, BashToolConfig? config = null)
    {
        var resolved = config ?? new BashToolConfig();
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var subprocess = ctx.Get<SubprocessService>(SubprocessService.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:bash", PromptOrders.ToolBash, SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = Description,
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["command"] = ToolSchemas.StringParam("The bash command to execute."),
                    ["description"] = ToolSchemas.StringParam(
                        "Clear, concise description of what this command does in active voice, "
                        + "5-10 words (shown in the UI). Examples: \"ls\" → \"List files in current directory\"; "
                        + "\"git status\" → \"Show working tree status\"; \"npm install\" → \"Install package dependencies\"."),
                    ["timeoutMs"] = ToolSchemas.NumberParam("Timeout in milliseconds. The executor applies its configured default and cap, and kills the command on expiry."),
                    ["workdir"] = ToolSchemas.StringParam("Working directory for this command. Defaults to the session workspace; a relative path is resolved against it."),
                },
                "command", "description"),
            Output = new ToolOutputDefinition(OutputSchema, (_, value) => Render(value)),
            Execute = (args, exec) => Execute(args, exec, subprocess, resolved),
        });
        return new CompositeDisposable(section, registration);
    }

    private static readonly System.Text.Json.Nodes.JsonObject OutputSchema = ToolSchemas.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["kind", "timedOut", "aborted", "timeoutMs", "stdout", "stderr"],
          "properties": {
            "kind": { "type": "string", "enum": ["foreground"] },
            "exitCode": { "type": ["integer", "null"] },
            "signal": { "type": ["string", "null"] },
            "timedOut": { "type": "boolean" },
            "aborted": { "type": "boolean" },
            "timeoutMs": { "type": "number" },
            "stdout": {
              "type": "object",
              "additionalProperties": false,
              "required": ["text", "truncated"],
              "properties": {
                "text": { "type": "string" },
                "truncated": { "type": "boolean" },
                "spillPath": { "type": "string" }
              }
            },
            "stderr": {
              "type": "object",
              "additionalProperties": false,
              "required": ["text", "truncated"],
              "properties": {
                "text": { "type": "string" },
                "truncated": { "type": "boolean" },
                "spillPath": { "type": "string" }
              }
            }
          }
        }
        """);

    private static async Task<object?> Execute(JsonElement args, ToolRunContext exec, SubprocessService subprocess, BashToolConfig config)
    {
        var command = args.GetProperty("command").GetString() ?? "";
        var description = args.GetProperty("description").GetString() ?? "";
        double? timeoutMsArg = args.TryGetProperty("timeoutMs", out var timeoutElement) && timeoutElement.ValueKind == JsonValueKind.Number
            ? timeoutElement.GetDouble()
            : null;
        var workdirArg = args.TryGetProperty("workdir", out var workdirElement) && workdirElement.ValueKind == JsonValueKind.String
            ? workdirElement.GetString()
            : null;
        if (command.Trim().Length == 0)
            throw new ArgumentException("invalid command: expected a non-empty string");
        if (description.Trim().Length == 0)
            throw new ArgumentException("invalid description: expected a non-empty string");
        if (timeoutMsArg is not null && (!double.IsFinite(timeoutMsArg.Value) || timeoutMsArg.Value <= 0))
            throw new ArgumentException($"invalid timeoutMs: expected a positive number, got {JsonSerializer.Serialize(timeoutMsArg.Value)}");
        var timeoutMs = (long)Math.Clamp(timeoutMsArg ?? config.TimeoutMs, 1, config.MaxTimeoutMs);
        var workdir = ResolveWorkdir(workdirArg, exec, config);
        using var timeoutSignal = new CancellationTokenSource();
        using var fused = CancellationTokenSource.CreateLinkedTokenSource(exec.Signal, timeoutSignal.Token);
        timeoutSignal.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        var env = EnvOverrides;
        var handle = subprocess.Spawn(new SubprocessSpawnSpec
        {
            Argv = ["bash", "-c", command],
            Cwd = workdir,
            Env = env,
            Stdout = new SubprocessCollect(config.MaxOutputBytes, config.MaxSpillBytes),
            Stderr = new SubprocessCollect(config.MaxOutputBytes, config.MaxSpillBytes),
            Signal = fused.Token,
        });
        var outcome = await handle.Done;
        var stdoutRead = handle.StdoutReader.ReadFrom(0);
        var stderrRead = handle.StderrReader.ReadFrom(0);
        var timedOut = timeoutSignal.IsCancellationRequested && !exec.Signal.IsCancellationRequested;
        if (!timedOut && fused.IsCancellationRequested)
            throw new HarnessException("tool call aborted", ToolErrorCodes.Aborted);
        return new BashRunValue(
            "foreground",
            outcome.ExitCode,
            outcome.Signal,
            timedOut,
            false,
            timeoutMs,
            new BashStreamOutput(stdoutRead.Text, stdoutRead.Lossy, stdoutRead.SpillPath),
            new BashStreamOutput(stderrRead.Text, stderrRead.Lossy, stderrRead.SpillPath));
    }

    private static string ResolveWorkdir(string? modelWorkdir, ToolRunContext exec, BashToolConfig config)
    {
        var sessionCwd = exec.Agent?.Session.Header.Cwd;
        if (modelWorkdir is not null)
        {
            if (Path.IsPathRooted(modelWorkdir)) return modelWorkdir;
            if (sessionCwd is not null) return Path.GetFullPath(Path.Combine(sessionCwd, modelWorkdir));
            return Path.GetFullPath(modelWorkdir);
        }
        return sessionCwd ?? config.Cwd ?? Environment.CurrentDirectory;
    }

    private static IReadOnlyList<ContentBlock> Render(JsonElement value)
    {
        var run = value.Deserialize<BashRunValue>(DshJson.Options)
            ?? throw new JsonException("bash result value is malformed");
        return [new TextBlock(RenderResult(run))];
    }

    internal static string RenderResult(BashRunValue result)
    {
        var outText = StreamText(result.Stdout);
        var errText = StreamText(result.Stderr);
        var body = outText;
        if (errText.Length > 0)
        {
            if (body.Length > 0 && !body.EndsWith('\n')) body += '\n';
            body += $"[stderr]\n{errText}";
        }
        if (body.Length == 0) body = "(no output)";
        var markers = new List<string>();
        if (result.TimedOut) markers.Add($"[timed out after {result.TimeoutMs}ms]");
        if (result.Signal is not null) markers.Add($"[killed by signal: {result.Signal}]");
        else if (result.ExitCode is not 0) markers.Add($"[exit code: {result.ExitCode}]");
        if (markers.Count == 0) return body;
        if (!body.EndsWith('\n')) body += '\n';
        return body + string.Join('\n', markers);
    }

    private static string StreamText(BashStreamOutput output)
        => output.Truncated
            ? $"{output.Text}\n[output truncated; full output: {output.SpillPath ?? "(unavailable)"}]"
            : output.Text;
}
