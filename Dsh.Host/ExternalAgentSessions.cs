using System.Collections.Concurrent;
using Dsh.AgentAdapters;
using Dsh.GitInspect;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Host;

/**
 * External agent sessions owned by the host (host.agent.* for everything but harness sessions).
 * Terminal mode runs the agent's TUI in the PTY daemon (external session, survives host restarts).
 * ACP mode runs the agent's ACP entry as a child process (acp session, exits with the host and can be resumed).
 * Only sessions started here are known; nothing is scanned or adopted.
 */
internal sealed class ExternalAgentSessions : IAsyncDisposable
{
    private readonly AcpSessionServices _services;
    private readonly HostPtySessions _ptys;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Context _ctx;
    private readonly ConcurrentDictionary<string, AcpAgentSession> _live = new(StringComparer.Ordinal);

    public ExternalAgentSessions(Context ctx, AcpSessionServices services, HostPtySessions ptys)
    {
        _ctx = ctx;
        _services = services;
        _ptys = ptys;
    }

    private HostSessionRegistry Registry => _services.Registry;

    /** The harness entry, every built-in adapter with its probed executables, and the two user-defined kinds. */
    public static async Task<IReadOnlyList<RemoteAgentCatalogEntry>> CatalogAsync(CancellationToken cancellationToken)
    {
        var probes = await Task.WhenAll(AgentAdapterCatalog.BuiltIn.Select(adapter => AgentExecutableLocator.ProbeAsync(adapter, cancellationToken))).ConfigureAwait(false);
        var builtIn = AgentAdapterCatalog.BuiltIn.Zip(probes, (adapter, probe) => new RemoteAgentCatalogEntry(
            adapter.Kind, adapter.DisplayName, HostSessionKinds.External, probe.CanRunInteractive || probe.CanRunAcp, probe.CanRunInteractive, probe.CanRunAcp,
            adapter.Syntax.ResumeLatest is not null || probe.CanRunAcp, false, probe.InteractivePath, probe.AcpPath, probe.Version));
        return
        [
            new RemoteAgentCatalogEntry(HostAgentKinds.Dsharp, "DeepSeek Harness", HostSessionKinds.Harness, true, false, false, true, false),
            .. builtIn,
            new RemoteAgentCatalogEntry(AgentKinds.Terminal, "Custom command", HostSessionKinds.External, true, true, false, false, true),
            new RemoteAgentCatalogEntry(AgentKinds.Acp, "Custom ACP agent", HostSessionKinds.Acp, true, false, true, true, true),
        ];
    }

    /**
     * The session's working directory: ProjectPath itself, or a git worktree of its repository.
     * An existing worktree at the requested path is reused; otherwise it is added (relative paths are taken from ProjectPath).
     */
    public static async Task<string> PrepareDirectoryAsync(string projectPath, RemoteWorktreeChoice? worktree, CancellationToken cancellationToken)
    {
        var project = HostPathPolicy.Expand(projectPath);
        if (!Directory.Exists(project))
            throw new DirectoryNotFoundException($"project directory does not exist: {project}");
        if (worktree is null)
            return project;
        var inspector = new GitInspector(project);
        var target = worktree.Path.StartsWith('~') ? HostPathPolicy.Expand(worktree.Path) : Path.GetFullPath(worktree.Path, project);
        var existing = (await inspector.ListWorktreesAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(Path.TrimEndingDirectorySeparator(candidate.Path), Path.TrimEndingDirectorySeparator(target), PathComparison));
        return existing?.Path ?? (await inspector.AddWorktreeAsync(new GitWorktreeAddRequest(target, worktree.NewBranch, worktree.StartPoint), cancellationToken).ConfigureAwait(false)).Path;
    }

    public bool IsAcp(string sessionId) => Registry.Get(sessionId)?.Kind == HostSessionKinds.Acp;

    public async Task<RemoteSessionSummary> StartAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken)
    {
        var directory = await PrepareDirectoryAsync(request.ProjectPath, request.Worktree, cancellationToken).ConfigureAwait(false);
        var adapter = AdapterFor(request);
        var mode = request.Mode ?? (request.AgentKind == AgentKinds.Acp ? RemoteAgentModes.Acp : RemoteAgentModes.Terminal);
        var command = mode switch
        {
            RemoteAgentModes.Acp => adapter.Acp ?? throw new NotSupportedException($"{adapter.DisplayName} has no ACP mode"),
            RemoteAgentModes.Terminal => TerminalCommand(adapter, request.InitialPrompt),
            _ => throw new ArgumentException($"unknown agent mode: {mode}"),
        };
        var launch = new HostAgentLaunch(command.Executable, command.Arguments, request.Environment);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var registration = new RemoteSessionSummary($"acp-{Guid.NewGuid():N}",
            mode == RemoteAgentModes.Acp ? HostSessionKinds.Acp : HostSessionKinds.External,
            adapter.Kind, directory, request.Title ?? adapter.DisplayName, HostSessionStatuses.Idle, now, now, 0, null);
        return mode == RemoteAgentModes.Acp
            ? await StartAcpAsync(registration, launch, request.InitialPrompt, cancellationToken).ConfigureAwait(false)
            : await StartTerminalAsync(registration, launch, request.Columns, request.Rows, cancellationToken).ConfigureAwait(false);
    }

    public void Prompt(string sessionId, string text) => Live(sessionId).Prompt(text);

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken) => Live(sessionId).CancelAsync(cancellationToken);

    /** Ends the agent process; the registry records the exit (with the exit code the process reported). */
    public async Task StopAsync(string sessionId, CancellationToken cancellationToken)
    {
        var record = Registry.Get(sessionId) ?? throw new InvalidOperationException($"unknown agent session: {sessionId}");
        if (record.Kind == HostSessionKinds.External)
        {
            await _ptys.StopAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!_live.TryRemove(sessionId, out var session))
            return;
        await session.DisposeAsync().ConfigureAwait(false);
    }

    /**
     * ACP sessions reopen the agent's own session under the same host session id.
     * Terminal-mode agents start again with their adapter's "continue the latest session" arguments in the same directory and come back
     * as a new session (the PTY daemon names PTYs); the exited record stays as it was.
     */
    public async Task<RemoteSessionSummary> ResumeAsync(string sessionId, CancellationToken cancellationToken)
    {
        var record = Registry.Get(sessionId) ?? throw new InvalidOperationException($"unknown agent session: {sessionId}");
        if (record.Status != HostSessionStatuses.Exited)
            throw new InvalidOperationException($"session {sessionId} is still running");
        var launch = Registry.AgentLaunch(sessionId) ?? throw new InvalidOperationException($"session {sessionId} has no agent launch command");
        if (record.Kind == HostSessionKinds.Acp)
            return await StartAcpAsync(record, launch, null, cancellationToken).ConfigureAwait(false);
        var adapter = AgentAdapterCatalog.Find(record.AgentKind!);
        var command = adapter?.ResumeCommand() ?? throw new NotSupportedException($"{record.Title ?? record.AgentKind} has no resume arguments");
        return await StartTerminalAsync(record, launch with { Executable = command.Executable, Arguments = command.Arguments }, null, null, cancellationToken).ConfigureAwait(false);
    }


    private async Task<RemoteSessionSummary> StartTerminalAsync(RemoteSessionSummary record, HostAgentLaunch launch, int? columns, int? rows, CancellationToken cancellationToken)
    {
        var executable = AgentExecutableLocator.Resolve(launch.Executable)
            ?? throw new FileNotFoundException($"agent executable not found on PATH: {launch.Executable}", launch.Executable);
        var start = new RemotePtyStartRequest(executable, launch.Arguments, record.ProjectPath, columns, rows, record.Title);
        var id = await _ptys.StartAgentAsync(start, record.ProjectPath!, record.AgentKind!, launch.Environment, cancellationToken).ConfigureAwait(false);
        return Registry.Get(id) ?? throw new InvalidOperationException($"session {id} was not registered");
    }

    /** Registered only after the agent accepted the session, so a failed start leaves nothing behind. */
    private async Task<RemoteSessionSummary> StartAcpAsync(RemoteSessionSummary record, HostAgentLaunch launch, string? initialPrompt, CancellationToken cancellationToken)
    {
        var command = new AgentCommand(launch.Executable, launch.Arguments);
        var session = await AcpAgentSession.StartAsync(record, command, launch.Environment, launch.AcpSessionId, _ctx, _services, cancellationToken).ConfigureAwait(false);
        Registry.Register(record with { Status = HostSessionStatuses.Idle, ExitCode = null }, launch with { AcpSessionId = session.AcpSessionId });
        _live[record.SessionId] = session;
        _ = WatchExitAsync(session);
        await Registry.FlushAsync().ConfigureAwait(false);
        if (initialPrompt is not null)
            session.Prompt(initialPrompt);
        return Registry.Get(record.SessionId)!;
    }

    /** An agent that exits by itself is released here; one stopped through StopAsync was already removed. */
    private async Task WatchExitAsync(AcpAgentSession session)
    {
        await session.Exited.ConfigureAwait(false);
        if (_live.TryRemove(new KeyValuePair<string, AcpAgentSession>(session.SessionId, session)))
            await session.DisposeAsync().ConfigureAwait(false);
    }


    private AcpAgentSession Live(string sessionId)
        => _live.TryGetValue(sessionId, out var session) ? session : throw new InvalidOperationException($"ACP session {sessionId} is not running");


    private static AgentAdapter AdapterFor(RemoteAgentStartRequest request)
    {
        if (AgentAdapterCatalog.Find(request.AgentKind) is { } builtIn)
            return builtIn;
        var command = request.Command is { } custom
            ? new AgentCommand(custom.Executable, custom.Arguments)
            : throw new ArgumentException($"agent kind {request.AgentKind} needs a command");
        var name = request.Title ?? Path.GetFileName(command.Executable);
        return request.AgentKind switch
        {
            AgentKinds.Terminal => AgentAdapterCatalog.Terminal(name, command),
            AgentKinds.Acp => AgentAdapterCatalog.GenericAcp(name, command),
            _ => throw new ArgumentException($"unknown agent kind: {request.AgentKind}"),
        };
    }

    /** Built-in agents take the prompt through their own syntax; a custom command takes it where an argument is exactly "{prompt}". */
    private static AgentCommand TerminalCommand(AgentAdapter adapter, string? prompt)
    {
        if (adapter.Kind != AgentKinds.Terminal)
            return adapter.InteractiveCommand(prompt) ?? throw new NotSupportedException($"{adapter.DisplayName} has no terminal mode");
        var command = adapter.Interactive!;
        if (prompt is not null && !command.Arguments.Contains(AgentLaunchSyntax.PromptToken))
            throw new ArgumentException($"the command takes no initial prompt: put {AgentLaunchSyntax.PromptToken} where it belongs among the arguments");
        return command with
        {
            Arguments = [.. command.Arguments.Where(argument => prompt is not null || argument != AgentLaunchSyntax.PromptToken)
                .Select(argument => argument == AgentLaunchSyntax.PromptToken ? prompt! : argument)],
        };
    }


    /** ACP agents end with the host (their sessions stay resumable); terminal-mode agents stay in the PTY daemon. */
    public async ValueTask DisposeAsync()
    {
        foreach (var id in _live.Keys)
        {
            if (_live.TryRemove(id, out var session))
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}

