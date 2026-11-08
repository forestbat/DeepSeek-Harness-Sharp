using System.Text.Json;
using Dsh.Runtime;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tools;

[global::Dsh.Plugins.DshPluginSettings]
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

/** The process one shell call starts: argv plus the environment overrides layered on the scrubbed parent environment. */
internal readonly record struct ShellLaunch(IReadOnlyList<string> Argv, IReadOnlyDictionary<string, string?> Env);

/**
 * One model-visible tool that runs each command in a fresh shell process.
 * bash, pwsh, powershell and wsl share parameters, timeout, output capture and rendering; they differ in prompt text, extra parameters and how a command becomes a process.
 */
internal sealed record ShellCommandDialect(
    string ToolName,
    string Description,
    string SectionText,
    string CommandDescription,
    string DescriptionExample,
    Func<string, JsonElement, IReadOnlyDictionary<string, string?>?, ShellLaunch> Launch)
{
    public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> ExtraParameters { get; init; } =
        new Dictionary<string, System.Text.Json.Nodes.JsonObject>();

    /** The shell exists only on Windows; elsewhere the tool is not registered at all. */
    public bool WindowsOnly { get; init; }

    /** Extra platform capability check (e.g. an executable that may be missing); false hides it from the model's tool list. */
    public Func<bool>? IsAvailable { get; init; }
}

public static class BashTool
{
    public const string ToolName = "bash";

    private const string SectionText = "Check the [exit code: N] marker on every bash result; investigate failures before moving on.";

    private const string Description =
        "Execute a bash command (`bash -c`) and return its stdout/stderr. "
        + "Each call runs in a fresh shell: no state (cwd, variables, functions) persists between calls — "
        + "pass `workdir` instead of using `cd`. Non-zero exits are reported as `[exit code: N]`. "
        + "On Windows this bash is Git-Bash (MSYS2), not WSL: drives appear as `/c/...`, Windows executables run directly, and Linux-only tools are missing — use the `wsl` tool for a real Linux distribution. "
        + "For PowerShell use `pwsh` (PowerShell 7, cross-platform) or, on Windows only, `powershell` (Windows PowerShell 5.1). "
        + "Current harness environment facts are exposed through managed `$DSH_*` variables; inspect them when needed. "
        + "Commands may run under a file sandbox; a blocked file operation is reported as `[sandbox: file access denied under <mode> mode]` — a policy denial, not a bug in the command; do not retry another way. "
        + "Long output is truncated to its tail; the full output is saved to a file whose path is reported when available. "
        + "Background execution is not available; long-running commands must finish within the timeout.";

    public static IDisposable Register(Context ctx, BashToolConfig? config = null)
        => RegisterShell(ctx, new ShellCommandDialect(
                ToolName,
                Description,
                SectionText,
                "The bash command to execute.",
                "\"npm install\" → \"Install package dependencies\"",
                (command, _, envs) => new ShellLaunch(["bash", "-c", command], MergeEnv(envs)))
            {
                // Windows 上 Git-Bash 与 pwsh 并存时模型会一直选 bash, 因此只把 bash 从可见工具里收起, 工具本身仍可被内部调用。
                IsAvailable = static () => !OperatingSystem.IsWindows(),
            },
            config ?? new BashToolConfig());

    internal static IDisposable RegisterShell(Context ctx, ShellCommandDialect dialect, BashToolConfig config)
    {
        if (dialect.WindowsOnly && !OperatingSystem.IsWindows())
            return new CompositeDisposable();
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var subprocess = ctx.Get<SubprocessService>(SubprocessService.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal($"tool:{dialect.ToolName}", dialect.SectionText));
        var parameters = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["command"] = ToolSchemas.StringParam(dialect.CommandDescription),
            ["description"] = ToolSchemas.StringParam(
                "Clear, concise description of what this command does in active voice, "
                + "5-10 words (shown in the UI). Examples: \"ls\" → \"List files in current directory\"; "
                + $"\"git status\" → \"Show working tree status\"; {dialect.DescriptionExample}."),
            ["timeoutMs"] = ToolSchemas.NumberParam("Timeout in milliseconds. The executor applies its configured default and cap, and kills the command on expiry."),
            ["workdir"] = ToolSchemas.StringParam("Working directory for this command. Defaults to the session working directory; a relative path is resolved against it."),
            ["envs"] = ToolSchemas.StringMapParam("Environment variables to set for this command (object of string values). `DSH_*` keys are ignored."),
        };
        foreach (var (name, schema) in dialect.ExtraParameters)
            parameters[name] = schema;
        var registration = tools.Register(new ToolDefinition
        {
            Name = dialect.ToolName,
            Description = dialect.Description,
            Parameters = ToolSchemas.ObjectSchema(parameters, "command"),
            Output = new ToolOutputDefinition(OutputSchema, (_, value) => Render(value, dialect.ToolName)),
            Execute = (args, exec) => Execute(args, exec, subprocess, config, dialect),
            IsAvailable = dialect.IsAvailable,
            WriteTargets = (args, exec) => [WorkdirOf(WorkdirArgument(args), exec, config)],
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

    private static async Task<object?> Execute(JsonElement args, ToolRunContext exec, SubprocessService subprocess, BashToolConfig config, ShellCommandDialect dialect)
    {
        var command = args.GetProperty("command").GetString() ?? "";
        double? timeoutMsArg = args.TryGetProperty("timeoutMs", out var timeoutElement) && timeoutElement.ValueKind == JsonValueKind.Number
            ? timeoutElement.GetDouble()
            : null;
        var workdirArg = WorkdirArgument(args);
        var envs = ReadEnvs(args);
        if (command.Trim().Length == 0)
            throw new ArgumentException("invalid command: expected a non-empty string");
        if (timeoutMsArg is not null && (!double.IsFinite(timeoutMsArg.Value) || timeoutMsArg.Value <= 0))
            throw new ArgumentException($"invalid timeoutMs: expected a positive number, got {JsonSerializer.Serialize(timeoutMsArg.Value)}");
        var timeoutMs = (long)Math.Clamp(timeoutMsArg ?? config.TimeoutMs, 1, config.MaxTimeoutMs);
        var workdir = ResolveWorkdir(workdirArg, exec, config);
        using var timeoutSignal = new CancellationTokenSource();
        using var fused = CancellationTokenSource.CreateLinkedTokenSource(exec.Signal, timeoutSignal.Token);
        timeoutSignal.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        var launch = dialect.Launch(command, args, envs);
        using var handle = await subprocess.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = launch.Argv,
            Cwd = workdir,
            Env = launch.Env,
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

    internal static string? WorkdirArgument(JsonElement args)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty("workdir", out var workdir)
           && workdir.ValueKind == JsonValueKind.String
            ? workdir.GetString()
            : null;

    /** The directory a new shell process starts in; the process may write anywhere below it, so it is also the call's write target. */
    internal static string WorkdirOf(string? modelWorkdir, ToolRunContext exec, BashToolConfig config)
    {
        var sessionCwd = exec.Agent?.Session.WorkingDirectory;
        return modelWorkdir switch
        {
            null => sessionCwd ?? config.Cwd ?? Environment.CurrentDirectory,
            _ when Path.IsPathRooted(modelWorkdir) => modelWorkdir,
            _ when sessionCwd is not null => Path.GetFullPath(Path.Combine(sessionCwd, modelWorkdir)),
            _ => Path.GetFullPath(modelWorkdir),
        };
    }

    internal static string ResolveWorkdir(string? modelWorkdir, ToolRunContext exec, BashToolConfig config)
    {
        var workdir = WorkdirOf(modelWorkdir, exec, config);
        if (!Directory.Exists(workdir))
            throw new ArgumentException($"invalid workdir: directory does not exist: {workdir}");
        return workdir;
    }

    internal static IReadOnlyDictionary<string, string?>? ReadEnvs(JsonElement args)
    {
        if (!args.TryGetProperty("envs", out var envElement)
            || envElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (envElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("invalid envs: expected an object of string values");
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in envElement.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                result[property.Name] = null;
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"invalid envs: \"{property.Name}\" must be a string or null");
            result[property.Name] = property.Value.GetString();
        }
        return result;
    }

    internal static IReadOnlyDictionary<string, string?> MergeEnv(IReadOnlyDictionary<string, string?>? envs)
    {
        if (envs is null || envs.Count == 0)
            return ShellEnvironment.NonInteractiveOverrides;
        var merged = new Dictionary<string, string?>(ShellEnvironment.NonInteractiveOverrides);
        foreach (var (key, value) in envs)
        {
            if (key.StartsWith("DSH_", StringComparison.OrdinalIgnoreCase))
                continue;
            merged[key] = value;
        }
        return merged;
    }

    private static IReadOnlyList<ContentBlock> Render(JsonElement value, string toolName)
    {
        var run = DshJson.Deserialize<BashRunValue>(value)
        ?? throw new JsonException($"{toolName} result value is malformed");
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
