using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Csx;

/** Runs existing script files through the installed dotnet-script frontend without rewriting their source. */
public sealed class ToolRunCsx
{
    public const string ToolName = "run_csx";
    public const long DefaultTimeoutMs = 120_000;
    public const long MaxTimeoutMs = 600_000;

    private readonly SubprocessService _subprocess;
    private readonly CsxPluginConfig _config;
    private readonly string _cwd;

    private ToolRunCsx(Context ctx, string cwd, CsxPluginConfig config)
    {
        _subprocess = ctx.Get<SubprocessService>(SubprocessService.ServiceName)
            ?? throw new InvalidOperationException("run_csx requires the subprocess service");
        _config = config;
        _cwd = Path.GetFullPath(cwd);
    }

    public static IDisposable Register(Context ctx, string cwd, CsxPluginConfig? config = null)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("run_csx requires the tools service");
        var tool = new ToolRunCsx(ctx, cwd, config ?? new CsxPluginConfig());
        return tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Execute an existing C# .csx script with the installed dotnet-script frontend in a fresh process. "
                + "Use C# script statements, declarations, #load and #r directives, not a run_code method body. "
                + "Script arguments are available through Args, and #load paths retain their normal script-file semantics. "
                + "Returns stdout, stderr and exit status; frontend failures are errors. "
                + "This is not a runtime sandbox and does not provide run_code tool bindings.",
            Parameters = Parameters(),
            Output = new ToolOutputDefinition(OutputSchema(), (_, value) => [new TextBlock(Render(value))]),
            Execute = tool.ExecuteAsync,
            WriteTargets = (args, exec) => [tool.Workdir(args, exec)],
            IsConcurrencySafe = _ => false,
        });
    }

    private static JsonObject Parameters() => JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "script": { "type": "string", "description": "Existing .csx file path, resolved against workdir." },
            "arguments": { "type": "array", "items": { "type": "string" }, "description": "Literal script arguments passed after --, without shell expansion." },
            "workdir": { "type": "string", "description": "Process working directory, relative to the session working directory when not absolute." },
            "timeoutMs": { "type": "integer", "description": "Wall-clock timeout in milliseconds, default 120000 and maximum 600000." }
          },
          "required": ["script"],
          "additionalProperties": false
        }
        """)!.AsObject();

    private static JsonObject OutputSchema() => JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "exitCode": { "type": "integer" },
            "stdout": { "type": "string" },
            "stderr": { "type": "string" },
            "stdoutTruncated": { "type": "boolean" },
            "stderrTruncated": { "type": "boolean" },
            "stdoutSpillPath": { "type": ["string", "null"] },
            "stderrSpillPath": { "type": ["string", "null"] }
          },
          "required": ["exitCode", "stdout", "stderr", "stdoutTruncated", "stderrTruncated", "stdoutSpillPath", "stderrSpillPath"],
          "additionalProperties": false
        }
        """)!.AsObject();

    private string Workdir(JsonElement args, ToolRunContext exec)
    {
        var root = exec.Agent?.Session.WorkingDirectory ?? _cwd;
        return args.TryGetProperty("workdir", out var value)
            ? Path.GetFullPath(RequiredString(value, "workdir"), root)
            : root;
    }

    private async Task<object?> ExecuteAsync(JsonElement args, ToolRunContext exec)
    {
        var workdir = Workdir(args, exec);
        if (!Directory.Exists(workdir))
            throw new ArgumentException($"run_csx workdir does not exist: {workdir}");
        var script = Path.GetFullPath(RequiredString(args.GetProperty("script"), "script"), workdir);
        if (!File.Exists(script))
            throw new FileNotFoundException($"run_csx script does not exist: {script}");
        var timeoutMs = DefaultTimeoutMs;
        if (args.TryGetProperty("timeoutMs", out var timeout))
        {
            if (!timeout.TryGetInt64(out timeoutMs) || timeoutMs <= 0)
                throw new ArgumentException("run_csx timeoutMs must be a positive integer");
            timeoutMs = Math.Min(timeoutMs, MaxTimeoutMs);
        }
        var argv = new List<string> { "", script, "--" };
        if (args.TryGetProperty("arguments", out var scriptArguments))
        {
            if (scriptArguments.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("run_csx arguments must be an array of strings");
            foreach (var argument in scriptArguments.EnumerateArray())
            {
                if (argument.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("run_csx arguments must contain only strings");
                argv.Add(argument.GetString()!);
            }
        }
        using var timeoutSignal = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        using var fused = CancellationTokenSource.CreateLinkedTokenSource(exec.Signal, timeoutSignal.Token);
        var command = _config.Executable;
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("run_csx executable must name an installed dotnet-script frontend");
        if (!Path.IsPathRooted(command) && (command.Contains('/') || command.Contains('\\')))
            command = Path.GetFullPath(command, _cwd);
        try
        {
            argv[0] = await _subprocess.ResolveExecutable(command, signal: fused.Token);
        }
        catch (FileNotFoundException error)
        {
            throw FrontendUnavailable(error.Message);
        }
        using var bootstrap = OperatingSystem.IsWindows() ? new CsxUtf8Bootstrap(script, workdir) : null;
        if (bootstrap is not null)
        {
            argv[1] = bootstrap.ScriptPath;
        }
        SubprocessHandle handle;
        try
        {
            handle = _subprocess.Spawn(new SubprocessSpawnSpec { Argv = argv, Cwd = workdir, Signal = fused.Token });
        }
        catch (Win32Exception error)
        {
            throw FrontendUnavailable(error.Message);
        }
        using (handle)
        {
            var outcome = await handle.Done;
            var stdout = handle.StdoutReader.ReadFrom(0);
            var stderr = handle.StderrReader.ReadFrom(0);
            var value = new JsonObject
            {
                ["exitCode"] = outcome.ExitCode,
                ["stdout"] = stdout.Text,
                ["stderr"] = stderr.Text,
                ["stdoutTruncated"] = stdout.Lossy,
                ["stderrTruncated"] = stderr.Lossy,
                ["stdoutSpillPath"] = stdout.SpillPath,
                ["stderrSpillPath"] = stderr.SpillPath,
            };
            exec.Signal.ThrowIfCancellationRequested();
            if (timeoutSignal.IsCancellationRequested)
                throw new HarnessException($"run_csx timeout after {timeoutMs}ms\n{Render(value)}", "CSX_TIMEOUT");
            if (outcome.Signal is not null || outcome.ExitCode != 0)
                throw new HarnessException($"dotnet-script failed{(outcome.Signal is null ? "" : $" with {outcome.Signal}")}\n{Render(value)}", "CSX_FRONTEND_FAILED");
            return value;
        }
    }

    private static string RequiredString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new ArgumentException($"run_csx {name} must be a non-empty string");

    private static HarnessException FrontendUnavailable(string reason)
        => new($"run_csx could not invoke dotnet-script: {reason}. "
            + "Install the optional dotnet-script .NET tool and expose dotnet-script on PATH, "
            + "or set plugins.\"@deepseek-ai/dsh-csx\".executable to its executable path.", "CSX_FRONTEND_UNAVAILABLE");

    private static string Render(JsonObject value)
        => Render(DshJson.ToElementRuntime(value));

    private static string Render(JsonElement value)
    {
        var text = new StringBuilder();
        foreach (var stream in (ReadOnlySpan<string>)["stdout", "stderr"])
        {
            if (value.GetProperty(stream).GetString() is { Length: > 0 } output)
                text.Append('[').Append(stream).Append("]\n").Append(output).Append('\n');
            if (value.GetProperty(stream == "stdout" ? "stdoutTruncated" : "stderrTruncated").GetBoolean())
            {
                text.Append('[').Append(stream).Append(" truncated");
                if (value.GetProperty(stream == "stdout" ? "stdoutSpillPath" : "stderrSpillPath").GetString() is { } spill)
                    text.Append("; full output: ").Append(spill);
                text.Append("]\n");
            }
        }
        var exitCode = value.GetProperty("exitCode");
        text.Append("[exit code: ");
        if (exitCode.ValueKind == JsonValueKind.Null)
            text.Append("unavailable");
        else
            text.Append(exitCode.GetInt32());
        text.Append(']');
        return text.ToString();
    }
}
