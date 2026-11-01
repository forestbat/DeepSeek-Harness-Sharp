using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dsh.Runtime;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Jobs;

/** Start and end lines that frame one command's output in the shell's stdout; the end line carries the exit status. */
internal sealed record PersistentShellMarkers(string Start, string End);

/**
 * What distinguishes one persistent shell from another: how the process starts and how a command is framed between markers.
 * Sessions per agent, queueing, timeouts, exit handling and truncation are shared by every dialect.
 */
internal sealed record PersistentShellDialect(
    string ToolName,
    string ShellLabel,
    string PackageLabel,
    string CommandDescription,
    string SearchHint,
    Func<CancellationToken, IReadOnlyList<string>> StartArgv,
    Func<string, PersistentShellMarkers, string> WrapCommand);

internal readonly record struct PersistentShellSettings(long TimeoutMs, int MaxOutputChars, string Description);

internal static class PersistentShellTool
{
    private const string LostPrefixMessage = "<response clipped><NOTE>The beginning of this command output was dropped by the terminal scrollback limit. The following text is the earliest retained output.</NOTE>\n";
    private const int PollIntervalMs = 25;
    private const int MinStdoutCaptureBytes = 64 * 1024;
    private const int StdoutBytesPerChar = 4;
    private const int StderrCaptureBytes = 64 * 1024;

    /** 冷启动不消耗命令执行预算；Git Bash 在 Windows 首次启动时可能需要数秒。 */
    private static readonly TimeSpan ShellStartupTimeout = TimeSpan.FromSeconds(30);

    private sealed record CapturedOutput(string Text, bool Incomplete, int? ExitCode);

    private sealed class PersistentShell : IDisposable
    {
        public required SubprocessHandle Handle { get; init; }
        public required string Policy { get; init; }
        public long Cursor { get; set; }

        public void Dispose() => Handle.Dispose();
    }

    private sealed class ShellRegistry : IDisposable
    {
        private readonly Dictionary<IAgent, PersistentShell> _live = [];
        private readonly Dictionary<IAgent, SemaphoreSlim> _queues = [];
        private readonly object _gate = new();

        public SemaphoreSlim QueueFor(IAgent owner)
        {
            lock (_gate)
                return _queues.TryGetValue(owner, out var queue) ? queue : _queues[owner] = new SemaphoreSlim(1, 1);
        }

        public PersistentShell? Get(IAgent owner)
        {
            lock (_gate)
                return _live.GetValueOrDefault(owner);
        }

        public void Set(IAgent owner, PersistentShell shell)
        {
            lock (_gate)
                _live[owner] = shell;
        }

        public void Reset(IAgent owner)
        {
            PersistentShell? shell;
            lock (_gate)
            {
                if (!_live.Remove(owner, out shell)) return;
            }
            shell.Handle.Terminate();
            shell.Dispose();
        }

        public void Dispose()
        {
            List<PersistentShell> shells;
            lock (_gate)
            {
                shells = [.. _live.Values];
                _live.Clear();
            }
            foreach (var shell in shells)
            {
                shell.Handle.Terminate();
                shell.Dispose();
            }
        }
    }

    /** Everything one command needs: the dialect, the limits and the owner's live shell lookup. */
    private sealed record CommandContext(
        PersistentShellDialect Dialect,
        PersistentShellSettings Settings,
        ShellRegistry Shells,
        Func<IAgent, Task<PersistentShell>> GetShell)
    {
        public string ResetMessage => $"The persistent {Dialect.ShellLabel} shell was reset; the next {Dialect.ShellLabel} call starts from the workspace with a fresh current directory and environment.";

        public string TruncatedMessage => $"<response clipped><NOTE>To save on context only part of this file has been shown to you. You should retry this tool after you have searched inside the file with {Dialect.SearchHint} in order to find the line numbers of what you are looking for.</NOTE>";
    }

    public static IDisposable Register(Context ctx, PersistentShellDialect dialect, PersistentShellSettings settings)
    {
        if (settings.TimeoutMs <= 0)
            throw new ArgumentException($"{dialect.PackageLabel}: timeoutMs must be a positive safe integer");
        if (settings.MaxOutputChars <= 0)
            throw new ArgumentException($"{dialect.PackageLabel}: maxOutputChars must be a positive safe integer");
        if (settings.Description.Trim().Length == 0)
            throw new ArgumentException($"{dialect.PackageLabel}: description must be non-empty");

        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var subprocess = ctx.Get<SubprocessService>(SubprocessService.ServiceName)!;
        var shells = new ShellRegistry();
        var effect = ctx.Effect(() => (Action)(() => shells.Dispose()), $"{dialect.PackageLabel} shell cleanup");

        async Task<PersistentShell> GetOrCreateShell(IAgent owner)
        {
            var policy = ctx.Get<IExternalProcessPolicy>(IExternalProcessPolicy.ServiceName, false)?.InvocationRevision ?? "";
            var existing = shells.Get(owner);
            if (existing is not null && !existing.Handle.Done.IsCompleted && existing.Policy == policy)
                return existing;
            if (existing is not null)
                shells.Reset(owner);
            var argv = dialect.StartArgv(CancellationToken.None);
            var cwd = owner.Session.WorkingDirectory ?? Environment.CurrentDirectory;
            var handle = await subprocess.SpawnAsync(new SubprocessSpawnSpec
            {
                Argv = argv,
                Cwd = cwd,
                Env = ShellEnvironment.NonInteractiveOverrides,
                ModelExecution = true,
                ExecutionKind = SubprocessExecutionKind.Tool,
                Stdout = new SubprocessCollect(Math.Max(MinStdoutCaptureBytes, settings.MaxOutputChars * StdoutBytesPerChar), null),
                Stderr = new SubprocessCollect(StderrCaptureBytes, null),
                KeepStandardInputOpen = true,
            });
            var shell = new PersistentShell { Handle = handle, Policy = policy, Cursor = 0 };
            shells.Set(owner, shell);
            owner.Ctx.Effect(() => (Action)(() => shells.Reset(owner)), $"{dialect.PackageLabel} owner cache cleanup");
            return shell;
        }

        var context = new CommandContext(dialect, settings, shells, GetOrCreateShell);
        var registration = tools.Register(new ToolDefinition
        {
            Name = dialect.ToolName,
            Description = settings.Description + IExternalProcessPolicy.SettingsProtectionNotice,
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("command"),
                ["properties"] = new JsonObject
                {
                    ["command"] = new JsonObject { ["type"] = "string", ["description"] = dialect.CommandDescription },
                },
            },
            Output = new ToolOutputDefinition(
                new JsonObject { ["type"] = "string" },
                (_, value) => [new TextBlock(value.GetString() ?? "")]),
            Execute = (args, exec) => Execute(context, args, exec),
            // Existing shells retain their own cwd; only a newly started process has a session-derived write target.
            WriteTargets = (_, exec) => exec.Agent is { } owner && shells.Get(owner) is not { Handle.Done.IsCompleted: false }
                ? [owner.Session.WorkingDirectory ?? Environment.CurrentDirectory]
                : [],
        });

        return new CompositeDisposable(registration, new ActionDisposable(() => effect.Dispose()), shells);
    }

    private static async Task<object?> Execute(CommandContext context, JsonElement args, ToolRunContext exec)
    {
        var command = args.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String
            ? commandElement.GetString() ?? ""
            : "";
        if (command.Trim().Length == 0)
            throw new ArgumentException("command must be a non-empty string");
        var owner = exec.Agent ?? throw new InvalidOperationException($"{context.Dialect.ShellLabel} requires an owning agent session");
        var queue = context.Shells.QueueFor(owner);
        await queue.WaitAsync(exec.Signal);
        try
        {
            exec.Signal.ThrowIfCancellationRequested();
            return await ExecuteCommand(context, owner, command, exec.Signal);
        }
        finally
        {
            queue.Release();
        }
    }

    private static async Task<string> ExecuteCommand(CommandContext context, IAgent owner, string command, CancellationToken upstream)
    {
        var shell = await context.GetShell(owner);
        var marker = MakeMarkers(context.Dialect.ShellLabel);
        var wrapped = context.Dialect.WrapCommand(command, marker);
        var budget = TimeSpan.FromMilliseconds(context.Settings.TimeoutMs);
        using var timeout = new CancellationTokenSource(budget > ShellStartupTimeout ? budget : ShellStartupTimeout);
        var commandStarted = false;

        shell.Cursor = shell.Handle.StdoutReader.ReadFrom(shell.Cursor).NextOffset;
        try
        {
            await shell.Handle.StandardInput.WriteLineAsync(wrapped);
        }
        catch (Exception)
        {
            context.Shells.Reset(owner);
            throw;
        }

        while (true)
        {
            if (shell.Handle.Done is { IsCompleted: true } done)
                return RespondToSessionExit(context, owner, shell, await done, marker);
            var read = shell.Handle.StdoutReader.ReadFrom(shell.Cursor);
            if (CommandOutput(read.Text, marker, read.Lossy) is { } captured)
                return RenderCaptured(captured, context);
            if (!commandStarted && read.Text.Contains(marker.Start, StringComparison.Ordinal))
            {
                commandStarted = true;
                timeout.CancelAfter(budget);
            }
            if (timeout.IsCancellationRequested)
                return RespondToTimeout(context, owner, PartialOutput(read.Text, marker, read.Lossy));
            if (upstream.IsCancellationRequested)
            {
                context.Shells.Reset(owner);
                throw new HarnessException("tool call aborted", ToolErrorCodes.Aborted);
            }
            await Task.Delay(PollIntervalMs, CancellationToken.None);
        }
    }

    private static string RespondToTimeout(CommandContext context, IAgent owner, CapturedOutput partial)
    {
        var rendered = RenderCaptured(partial, context);
        context.Shells.Reset(owner);
        return string.Join('\n',
            $"Your command timed out after {Math.Round(context.Settings.TimeoutMs / 1000.0, MidpointRounding.AwayFromZero)} seconds or experienced an OOM error. Below is partial output:",
            rendered,
            context.ResetMessage);
    }

    private static string RespondToSessionExit(
        CommandContext context,
        IAgent owner,
        PersistentShell shell,
        SubprocessOutcome outcome,
        PersistentShellMarkers marker)
    {
        var read = shell.Handle.StdoutReader.ReadFrom(shell.Cursor);
        var partial = PartialOutput(read.Text, marker, read.Lossy);
        var content = RenderShellExitStatus(RenderCaptured(partial, context), outcome.ExitCode, outcome.Signal);
        context.Shells.Reset(owner);
        return string.Join('\n', new[] { content, context.ResetMessage }.Where(part => part.Length > 0));
    }

    private static PersistentShellMarkers MakeMarkers(string shellLabel)
    {
        var nonce = Guid.NewGuid().ToString();
        var prefix = $"__DSH_PERSISTENT_{shellLabel.ToUpperInvariant()}";
        return new PersistentShellMarkers($"{prefix}_START_{nonce}__", $"{prefix}_END_{nonce}:");
    }

    private static readonly Regex EndStatusPattern = new(@"^(-?\d+)\r?\n", RegexOptions.Compiled);

    private static string TrimTrailingNewline(string text)
        => Regex.Replace(text, @"\r?\n$", "");

    private static string TrimLeadingNewline(string text)
        => Regex.Replace(text, @"^\r?\n", "");

    private static CapturedOutput? CommandOutput(string text, PersistentShellMarkers marker, bool lossy)
    {
        var end = text.LastIndexOf(marker.End, StringComparison.Ordinal);
        if (end < 0) return null;
        var statusMatch = EndStatusPattern.Match(text[(end + marker.End.Length)..]);
        if (!statusMatch.Success) return null;
        var startMarker = text.LastIndexOf(marker.Start, end, StringComparison.Ordinal);
        var start = startMarker < 0 ? 0 : startMarker + marker.Start.Length;
        return new CapturedOutput(
            TrimTrailingNewline(TrimLeadingNewline(text[start..end])),
            lossy || startMarker < 0,
            int.Parse(statusMatch.Groups[1].Value));
    }

    private static CapturedOutput PartialOutput(string text, PersistentShellMarkers marker, bool lossy)
    {
        var startMarker = text.LastIndexOf(marker.Start, StringComparison.Ordinal);
        if (startMarker >= 0)
        {
            var afterStart = TrimLeadingNewline(text[(startMarker + marker.Start.Length)..]);
            var endIndex = afterStart.LastIndexOf(marker.End, StringComparison.Ordinal);
            var beforeEnd = endIndex < 0 ? afterStart : afterStart[..endIndex];
            return new CapturedOutput(TrimTrailingNewline(beforeEnd), lossy, null);
        }
        return new CapturedOutput(TrimTrailingNewline(text), true, null);
    }

    private static string MaybeTruncate(string content, CommandContext context, bool incomplete)
    {
        var maxOutputChars = context.Settings.MaxOutputChars;
        if (content.Length <= maxOutputChars && !incomplete) return content;
        return content.Length <= maxOutputChars
            ? content + context.TruncatedMessage
            : content[..maxOutputChars] + context.TruncatedMessage;
    }

    private static string RenderCaptured(CapturedOutput output, CommandContext context)
    {
        var rendered = MaybeTruncate(output.Text, context, output.Incomplete);
        var withPrefix = output.Incomplete && output.Text.Length > 0
            ? LostPrefixMessage + rendered
            : rendered;
        var marker = output.ExitCode is not null and not 0
            ? $"[exit code: {output.ExitCode}]"
            : null;
        return AppendStatusMarker(withPrefix, marker);
    }

    private static string AppendStatusMarker(string content, string? marker)
    {
        if (marker is null) return content;
        return content.Length == 0 ? marker : $"{content}\n{marker}";
    }

    private static string RenderShellExitStatus(string content, int? exitCode, string? signal)
    {
        var marker = signal is not null
            ? $"[shell killed by signal: {signal}]"
            : exitCode is not null
                ? $"[shell exited: code {exitCode}]"
                : "[shell exited]";
        return AppendStatusMarker(content, marker);
    }

    private sealed class CompositeDisposable(params IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            dispose();
        }
    }
}
