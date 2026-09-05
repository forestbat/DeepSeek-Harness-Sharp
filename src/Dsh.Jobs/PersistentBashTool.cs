using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cordis;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Jobs;

public sealed record PersistentBashConfig
{
    public const string DefaultDescription = "Run commands in a persistent bash shell. State, including the current directory and exported environment variables, persists across calls for this agent.";

    public string? BashPath { get; init; }
    public long TimeoutMs { get; init; } = 300_000;
    public int MaxOutputChars { get; init; } = 16_000;
    public string Description { get; init; } = DefaultDescription;
}

public static class BashDiscovery
{
    public static IReadOnlyList<string> BashCandidates(Func<string, string?> env, string? gitExe)
    {
        var candidates = new List<string>();
        if (gitExe is not null && (gitExe.Contains('/') || gitExe.Contains('\\')))
        {
            var dir = Path.GetDirectoryName(gitExe)!;
            var root = Path.GetDirectoryName(dir)!;
            candidates.Add(Path.Combine(root, "bin", "bash.exe"));
            candidates.Add(Path.Combine(dir, "bash.exe"));
            var grandRoot = Path.GetDirectoryName(root);
            if (grandRoot is not null)
                candidates.Add(Path.Combine(grandRoot, "bin", "bash.exe"));
        }
        if (env("ProgramFiles") is { } programFiles)
            candidates.Add(Path.Combine(programFiles, "Git", "bin", "bash.exe"));
        if (env("ProgramFiles(x86)") is { } programFilesX86)
            candidates.Add(Path.Combine(programFilesX86, "Git", "bin", "bash.exe"));
        if (env("LOCALAPPDATA") is { } localAppData)
            candidates.Add(Path.Combine(localAppData, "Programs", "Git", "bin", "bash.exe"));
        if (env("USERPROFILE") is { } userProfile)
            candidates.Add(Path.Combine(userProfile, "scoop", "apps", "git", "current", "bin", "bash.exe"));
        return candidates.Distinct().ToList();
    }
}

public sealed class BashResolver(SubprocessService subprocess, string? explicitBashPath)
{
    private string? _inferred;

    public string Resolve(CancellationToken signal)
    {
        if (!string.IsNullOrEmpty(explicitBashPath))
            return subprocess.ResolveExecutable(explicitBashPath, null, signal).GetAwaiter().GetResult();
        if (_inferred is not null)
            return subprocess.ResolveExecutable(_inferred, null, signal).GetAwaiter().GetResult();
        string? gitExe = null;
        try
        {
            gitExe = subprocess.ResolveExecutable("git", null, signal).GetAwaiter().GetResult();
        }
        catch (FileNotFoundException)
        {
        }
        foreach (var candidate in BashDiscovery.BashCandidates(Environment.GetEnvironmentVariable, gitExe))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                _inferred = subprocess.ResolveExecutable(candidate, null, signal).GetAwaiter().GetResult();
                return _inferred;
            }
            catch (Exception)
            {
            }
        }
        try
        {
            return subprocess.ResolveExecutable("bash", null, signal).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"bash executable not found — install Git for Windows, expose a bash on PATH, or set the tool-bash-persistent `bashPath` config ({error.Message})");
        }
    }
}

public static class PersistentBashTool
{
    public const string ToolName = "bash";

    private const string TruncatedMessage = "<response clipped><NOTE>To save on context only part of this file has been shown to you. You should retry this tool after you have searched inside the file with `grep -n` in order to find the line numbers of what you are looking for.</NOTE>";
    private const string LostPrefixMessage = "<response clipped><NOTE>The beginning of this command output was dropped by the terminal scrollback limit. The following text is the earliest retained output.</NOTE>\n";
    private const string ShellResetMessage = "The persistent bash shell was reset; the next bash call starts from the workspace with a fresh current directory and environment.";
    private const int PollIntervalMs = 25;

    private sealed record CommandMarkers(string Start, string End);

    private sealed record CapturedOutput(string Text, bool Incomplete, int? ExitCode);

    private sealed class PersistentShell : IDisposable
    {
        public required SubprocessHandle Handle { get; init; }
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
                return _live.TryGetValue(owner, out var shell) ? shell : null;
        }

        public void Set(IAgent owner, PersistentShell shell)
        {
            lock (_gate)
                _live[owner] = shell;
        }

        public void Reset(IAgent owner, string reason)
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
                shells = [.._live.Values];
                _live.Clear();
            }
            foreach (var shell in shells)
            {
                shell.Handle.Terminate();
                shell.Dispose();
            }
        }
    }

    public static IDisposable Register(Context ctx, PersistentBashConfig? config = null)
    {
        var resolved = config ?? new PersistentBashConfig();
        if (resolved.TimeoutMs <= 0)
            throw new ArgumentException("tool-bash-persistent: timeoutMs must be a positive safe integer");
        if (resolved.MaxOutputChars <= 0)
            throw new ArgumentException("tool-bash-persistent: maxOutputChars must be a positive safe integer");
        if (resolved.Description.Trim().Length == 0)
            throw new ArgumentException("tool-bash-persistent: description must be non-empty");

        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var subprocess = ctx.Get<SubprocessService>(SubprocessService.ServiceName)!;
        var shells = new ShellRegistry();
        var resolver = new BashResolver(subprocess, resolved.BashPath);

        var effect = ctx.Effect(() => (Action)(() => shells.Dispose()), "tool-bash-persistent shell cleanup");

        PersistentShell GetOrCreateShell(IAgent owner)
        {
            var existing = shells.Get(owner);
            if (existing is not null && !existing.Handle.Done.IsCompleted)
                return existing;
            if (existing is not null)
                shells.Reset(owner, "persistent bash shell exited");
            var shellPath = resolver.Resolve(CancellationToken.None);
            var cwd = owner.Session.Header.Cwd ?? Environment.CurrentDirectory;
            var handle = subprocess.Spawn(new SubprocessSpawnSpec
            {
                Argv = [shellPath],
                Cwd = cwd,
                Env = BashTool.EnvOverridesShared,
                Stdout = new SubprocessCollect(Math.Max(64 * 1024, resolved.MaxOutputChars * 4), null),
                Stderr = new SubprocessCollect(64 * 1024, null),
                RedirectStandardInput = true,
            });
            var shell = new PersistentShell { Handle = handle, Cursor = 0 };
            shells.Set(owner, shell);
            owner.Ctx.Effect(() => (Action)(() => shells.Reset(owner, "owner disposed")), "tool-bash-persistent owner cache cleanup");
            return shell;
        }

        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = resolved.Description,
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["command"],
                  "properties": {
                    "command": {
                      "type": "string",
                      "description": "The bash command to run. Relative path is preferred in the command."
                    }
                  }
                }
                """)!.AsObject(),
            Output = new ToolOutputDefinition(
                JsonNode.Parse("""{ "type": "string" }""")!.AsObject(),
                (_, value) => [new TextBlock(value.GetString() ?? "")]),
            Execute = async (args, exec) =>
            {
                var command = args.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String
                    ? commandElement.GetString() ?? ""
                    : "";
                if (command.Trim().Length == 0)
                    throw new ArgumentException("command must be a non-empty string");
                var owner = exec.Agent ?? throw new InvalidOperationException("bash requires an owning agent session");
                var queue = shells.QueueFor(owner);
                await queue.WaitAsync(exec.Signal);
                try
                {
                    exec.Signal.ThrowIfCancellationRequested();
                    return await ExecuteCommand(shells, GetOrCreateShell, owner, command, resolved, exec.Signal);
                }
                finally
                {
                    queue.Release();
                }
            },
        });

        return new CompositeDisposable(registration, new ActionDisposable(() => effect.Dispose()), shells);
    }

    private static async Task<string> ExecuteCommand(
        ShellRegistry shells,
        Func<IAgent, PersistentShell> getShell,
        IAgent owner,
        string command,
        PersistentBashConfig config,
        CancellationToken upstream)
    {
        var shell = getShell(owner);
        var marker = MakeMarkers();
        var wrapped = WrapCommand(command, marker);
        using var timeout = new CancellationTokenSource();
        timeout.CancelAfter(TimeSpan.FromMilliseconds(config.TimeoutMs));
        using var fused = CancellationTokenSource.CreateLinkedTokenSource(upstream, timeout.Token);

        shell.Cursor = shell.Handle.StdoutReader.ReadFrom(shell.Cursor).NextOffset;
        try
        {
            shell.Handle.StandardInput.WriteLine(wrapped);
        }
        catch (Exception)
        {
            shells.Reset(owner, "persistent bash send failed");
            throw;
        }

        while (true)
        {
            if (shell.Handle.Done is { IsCompleted: true } done)
                return await RespondToSessionExit(shells, owner, shell, await done, marker, config);
            var read = shell.Handle.StdoutReader.ReadFrom(shell.Cursor);
            if (CommandOutput(read.Text, marker, read.Lossy) is { } captured)
                return RenderCaptured(captured, config.MaxOutputChars);
            if (timeout.IsCancellationRequested)
            {
                var partial = RenderCaptured(PartialOutput(read.Text, marker, read.Lossy), config.MaxOutputChars);
                shells.Reset(owner, "persistent bash command timed out");
                return string.Join('\n',
                    $"Your command timed out after {Math.Round(config.TimeoutMs / 1000.0, MidpointRounding.AwayFromZero)} seconds or experienced an OOM error. Below is partial output:",
                    partial,
                    ShellResetMessage);
            }
            if (upstream.IsCancellationRequested)
            {
                shells.Reset(owner, "persistent bash command aborted");
                throw new HarnessException("tool call aborted", ToolErrorCodes.Aborted);
            }
            await Task.Delay(PollIntervalMs, CancellationToken.None);
        }
    }

    private static async Task<string> RespondToSessionExit(
        ShellRegistry shells,
        IAgent owner,
        PersistentShell shell,
        SubprocessOutcome outcome,
        CommandMarkers marker,
        PersistentBashConfig config)
    {
        var read = shell.Handle.StdoutReader.ReadFrom(shell.Cursor);
        var partial = PartialOutput(read.Text, marker, read.Lossy);
        var content = RenderShellExitStatus(RenderCaptured(partial, config.MaxOutputChars), outcome.ExitCode, outcome.Signal);
        shells.Reset(owner, "persistent bash shell exited");
        return string.Join('\n', new[] { content, ShellResetMessage }.Where(part => part.Length > 0));
    }

    private static CommandMarkers MakeMarkers()
    {
        var nonce = Guid.NewGuid().ToString();
        return new CommandMarkers($"__DSH_PERSISTENT_BASH_START_{nonce}__", $"__DSH_PERSISTENT_BASH_END_{nonce}:");
    }

    private static string QuoteForBash(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
        return $"$'{escaped}'";
    }

    private static string WrapCommand(string command, CommandMarkers marker)
        => $"printf '%s\\n' {QuoteForBash(marker.Start)}; eval -- {QuoteForBash(command)} 2>&1; __dsh_persistent_bash_status=$?; printf '%s%s\\n' {QuoteForBash(marker.End)} \"$__dsh_persistent_bash_status\"";

    private static readonly Regex EndStatusPattern = new(@"^(\d+)\r?\n", RegexOptions.Compiled);

    private static string TrimTrailingNewline(string text)
        => Regex.Replace(text, @"\r?\n$", "");

    private static string TrimLeadingNewline(string text)
        => Regex.Replace(text, @"^\r?\n", "");

    private static CapturedOutput? CommandOutput(string text, CommandMarkers marker, bool lossy)
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

    private static CapturedOutput PartialOutput(string text, CommandMarkers marker, bool lossy)
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

    private static string MaybeTruncate(string content, int maxOutputChars, bool incomplete = false)
    {
        if (content.Length <= maxOutputChars && !incomplete) return content;
        return content.Length <= maxOutputChars
            ? content + TruncatedMessage
            : content[..maxOutputChars] + TruncatedMessage;
    }

    private static string RenderCaptured(CapturedOutput output, int maxOutputChars)
    {
        var rendered = MaybeTruncate(output.Text, maxOutputChars, output.Incomplete);
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
