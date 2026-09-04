using System.Text.Json;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tools;

public static class PwshTool
{
    public const string ToolName = "pwsh";

    private const string SectionText =
        "Non-zero exits are reported as `[exit code: N]` markers; investigate failures before moving on. "
        + "On Windows a killed process settles as `[exit code: 1]` without a signal marker; treat a bare exit 1 after an interruption as a termination, not a command failure.";

    private const string Description =
        "Execute a PowerShell command (`pwsh -Command`) and return its stdout/stderr. "
        + "Each call runs in a fresh pwsh process: no state (cwd, variables, functions) persists between calls — "
        + "pass `workdir` instead of using `cd`. Paths use native Windows form (`C:\\...`); read environment "
        + "variables with `$env:NAME`. Non-zero exits are reported as `[exit code: N]`. "
        + "Current harness environment facts are exposed through managed `$env:DSH_*` variables; inspect them when needed. "
        + "Commands may run under a file sandbox; a blocked file operation is reported as `[sandbox: file access denied under <mode> mode]` — a policy denial, not a bug in the command; do not retry another way. "
        + "Long output is truncated to its tail; the full output is saved to a file whose path is reported when available. "
        + "On Windows a force-killed command settles as `[exit code: 1]` without a signal marker — treat it as an interruption, not a command failure. "
        + "Background execution is not available; long-running commands must finish within the timeout.";

    // Windows PowerShell 5.1 默认按 OEM 代码页输出,非 ASCII 会乱码;每个命令行首钉 UTF-8(与 TS pwsh-local 相同)。
    private const string EncodingPreamble =
        "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [System.Text.UTF8Encoding]::new($false); ";

    public static IDisposable Register(Context ctx, BashToolConfig? config = null)
    {
        var resolved = config ?? new BashToolConfig();
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var subprocess = ctx.Get<SubprocessService>(SubprocessService.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:pwsh", PromptOrders.ToolPwsh, SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = Description,
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["command"] = ToolSchemas.StringParam("The PowerShell command to execute."),
                    ["description"] = ToolSchemas.StringParam(
                        "Clear, concise description of what this command does in active voice, "
                        + "5-10 words (shown in the UI). Examples: \"ls\" → \"List files in current directory\"; "
                        + "\"git status\" → \"Show working tree status\"; \"Get-Process\" → \"List running processes\"."),
                    ["timeoutMs"] = ToolSchemas.NumberParam("Timeout in milliseconds. The executor applies its configured default and cap, and kills the command on expiry."),
                    ["workdir"] = ToolSchemas.StringParam("Working directory for this command. Defaults to the session workspace; a relative path is resolved against it."),
                },
                "command", "description"),
            Output = new ToolOutputDefinition(BashTool.OutputSchemaShared, (_, value) => Render(value)),
            Execute = (args, exec) => Execute(args, exec, subprocess, resolved),
        });
        return new CompositeDisposable(section, registration);
    }

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
        var sessionCwd = exec.Agent?.Session.Header.Cwd;
        var workdir = workdirArg is not null
            ? Path.IsPathRooted(workdirArg) ? workdirArg : Path.GetFullPath(Path.Combine(sessionCwd ?? Environment.CurrentDirectory, workdirArg))
            : sessionCwd ?? config.Cwd ?? Environment.CurrentDirectory;
        using var timeoutSignal = new CancellationTokenSource();
        using var fused = CancellationTokenSource.CreateLinkedTokenSource(exec.Signal, timeoutSignal.Token);
        timeoutSignal.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        var handle = subprocess.Spawn(new SubprocessSpawnSpec
        {
            Argv = ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", EncodingPreamble + command],
            Cwd = workdir,
            Env = BashTool.EnvOverridesShared,
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

    private static IReadOnlyList<ContentBlock> Render(JsonElement value)
    {
        var run = value.Deserialize<BashRunValue>(DshJson.Options)
            ?? throw new JsonException("pwsh result value is malformed");
        return [new TextBlock(BashTool.RenderResult(run))];
    }
}
