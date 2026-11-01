using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.CSharpRepl;

/** Runs CSharpRepl's collected-stdin protocol in an owned subprocess. */
public static class ToolRunCSharpRepl
{
    public const string ToolName = "run_csharp_repl";
    private const long DefaultTimeoutMs = 120_000;
    private const long MaxTimeoutMs = 600_000;

    public static IDisposable Register(Context ctx, CSharpReplConfig? config = null)
    {
        var resolved = config ?? new CSharpReplConfig();
        resolved.Validate();
        var command = resolved.Executable;
        if (!Path.IsPathRooted(command) && (command.Contains('/') || command.Contains('\\')))
            command = Path.GetFullPath(command);
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("run_csharp_repl requires the tools service");
        var subprocess = ctx.Get<SubprocessService>(SubprocessService.ServiceName)
            ?? throw new InvalidOperationException("run_csharp_repl requires the subprocess service");
        return tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Evaluate C# using the independently installed CSharpRepl frontend and return stdout, stderr and the final expression. "
                + "Submit one complete multiline C# script; declarations and state are shared within that program, not between calls. "
                + "The process receives the program through redirected stdin followed by EOF, without an interactive prompt. "
                + "This is ordinary local code execution, not a sandbox or the run_code tool SDK. "
                + "Compilation and runtime failures are reported as tool errors with the frontend's output. "
                + "CSharpRepl must already be installed; it is never downloaded automatically.",
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["code", "description"],
                  "properties": {
                    "code": { "type": "string", "description": "Complete C# script, optionally ending in an expression to print." },
                    "description": { "type": "string", "description": "Short description of what the program does." },
                    "workdir": { "type": "string", "description": "Working directory, relative to the session directory when not absolute." },
                    "timeoutMs": { "type": "number", "description": "Wall-clock timeout in milliseconds, default 120000, maximum 600000." }
                  }
                }
                """)!.AsObject(),
            Output = new ToolOutputDefinition(OutputSchema, (_, value) => [new TextBlock(Render(value))]),
            Execute = (args, exec) => ExecuteAsync(args, exec, subprocess, command),
            WriteTargets = (args, exec) => [Workdir(args, exec)],
            IsConcurrencySafe = _ => false,
        });
    }

    private static readonly JsonObject OutputSchema = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["exitCode", "managedExitCode", "stdout", "stderr"],
          "properties": {
            "exitCode": { "type": "integer" },
            "managedExitCode": { "type": "integer" },
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
        """)!.AsObject();

    private static async Task<object?> ExecuteAsync(JsonElement args, ToolRunContext exec, SubprocessService subprocess, string command)
    {
        var code = args.GetProperty("code").GetString();
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("run_csharp_repl requires a non-empty code string");
        var workdir = Workdir(args, exec);
        if (!Directory.Exists(workdir))
            throw new ArgumentException($"invalid workdir: directory does not exist: {workdir}");
        var timeoutMs = Timeout(args);
        string executable;
        try
        {
            executable = await subprocess.ResolveExecutable(command, signal: exec.Signal);
        }
        catch (FileNotFoundException)
        {
            throw new HarnessException(
                "CSharpRepl is unavailable: install the optional CSharpRepl .NET tool separately and put csharprepl on PATH, "
                + "or set the plugin's executable parameter to its absolute path.",
                "CSHARP_REPL_MISSING");
        }

        using var status = new CSharpReplExitStatus();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        using var fused = CancellationTokenSource.CreateLinkedTokenSource(exec.Signal, timeout.Token);
        using var handle = subprocess.Spawn(new SubprocessSpawnSpec
        {
            Argv = [executable, status.PreloadPath],
            Cwd = workdir,
            KeepStandardInputOpen = true,
            Signal = fused.Token,
        });
        try
        {
            Exception? inputError = null;
            try
            {
                // CSharpRepl v0.9.2 Program and PipedInputEvaluator collect redirected stdin until EOF.
                // https://github.com/waf/CSharpRepl/blob/v0.9.2/CSharpRepl/Repls/PipedInputEvaluator.cs
                await handle.StandardInput.WriteAsync(code.AsMemory(), fused.Token);
                await handle.StandardInput.FlushAsync(fused.Token);
                handle.StandardInput.Close();
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                // Preserve frontend diagnostics when it exits before accepting all input.
                inputError = error;
            }

            var outcome = await handle.Done;
            int? managedExitCode = null;
            string? statusError = null;
            if (!fused.IsCancellationRequested)
            {
                try
                {
                    managedExitCode = await status.ReadAsync(exec.Signal);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
                {
                    statusError = error.Message;
                }
            }
            var value = new JsonObject
            {
                ["exitCode"] = outcome.ExitCode,
                ["managedExitCode"] = managedExitCode,
                ["stdout"] = StreamValue(handle.StdoutReader.ReadFrom(0)),
                ["stderr"] = StreamValue(handle.StderrReader.ReadFrom(0)),
            };
            if (exec.Signal.IsCancellationRequested || timeout.IsCancellationRequested
                || outcome.ExitCode is not 0 || outcome.Signal is not null || inputError is not null
                || managedExitCode is not null and not 0 || statusError is not null)
            {
                using var document = JsonDocument.Parse(value.ToJsonString());
                var output = Render(document.RootElement);
                if (exec.Signal.IsCancellationRequested)
                    throw new HarnessException($"CSharpRepl execution aborted\n{output}", ToolErrorCodes.Aborted);
                if (timeout.IsCancellationRequested)
                    throw new HarnessException($"CSharpRepl execution timed out after {timeoutMs}ms\n{output}", "CSHARP_REPL_TIMEOUT");
                if (outcome.ExitCode is not 0 || outcome.Signal is not null || managedExitCode is not null and not 0)
                    throw new HarnessException($"CSharpRepl failed [managed exit code: {managedExitCode?.ToString() ?? "unavailable"}; process exit code: {outcome.ExitCode?.ToString() ?? "none"}; signal: {outcome.Signal ?? "none"}]\n{output}", "CSHARP_REPL_FAILED");
                if (statusError is not null)
                    throw new HarnessException($"CSharpRepl exit status unavailable: {statusError}\n{output}", "CSHARP_REPL_STATUS");
                throw new HarnessException($"CSharpRepl could not receive the program: {inputError!.Message}\n{output}", "CSHARP_REPL_INPUT");
            }
            return value;
        }
        finally
        {
            handle.Terminate();
            await handle.Done;
        }
    }

    private static long Timeout(JsonElement args)
    {
        if (!args.TryGetProperty("timeoutMs", out var element))
            return DefaultTimeoutMs;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value) || !double.IsFinite(value) || value <= 0)
            throw new ArgumentException("timeoutMs must be a positive finite number");
        return (long)Math.Clamp(value, 1, MaxTimeoutMs);
    }

    private static string Workdir(JsonElement args, ToolRunContext exec)
    {
        var root = exec.Agent?.Session.WorkingDirectory ?? Environment.CurrentDirectory;
        if (!args.TryGetProperty("workdir", out var element))
            return Path.GetFullPath(root);
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("workdir must be a non-empty directory path");
        return Path.GetFullPath(value, root);
    }

    private static JsonObject StreamValue(SubprocessOutputRead stream)
    {
        var result = new JsonObject { ["text"] = stream.Text, ["truncated"] = stream.Lossy };
        if (stream.SpillPath is not null)
            result["spillPath"] = stream.SpillPath;
        return result;
    }

    private static string Render(JsonElement value)
    {
        var output = new StringBuilder();
        foreach (var name in (ReadOnlySpan<string>)["stdout", "stderr"])
        {
            var stream = value.GetProperty(name);
            var text = stream.GetProperty("text").GetString();
            if (!string.IsNullOrEmpty(text))
            {
                if (output.Length > 0 && output[^1] != '\n')
                    output.AppendLine();
                if (name == "stderr")
                    output.AppendLine("[stderr]");
                output.Append(text);
            }
            if (stream.GetProperty("truncated").GetBoolean())
            {
                output.Append("\n[output truncated; full output: ")
                    .Append(stream.TryGetProperty("spillPath", out var spill) ? spill.GetString() : "unavailable")
                    .AppendLine("]");
            }
        }
        if (output.Length == 0)
            output.Append("(no output)");
        if (output[^1] != '\n')
            output.AppendLine();
        output.Append("[exitCode: ").Append(value.GetProperty("exitCode").GetRawText())
            .Append("; managedExitCode: ").Append(value.GetProperty("managedExitCode").GetRawText()).Append(']');
        return output.ToString();
    }
}
