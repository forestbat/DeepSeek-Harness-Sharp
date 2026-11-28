using System.Collections.Concurrent;
using System.Text;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Workspace;

namespace Dsh.Fleet;

public sealed class FleetAgentExecutionBackend : IAgentExecutionBackend, IDisposable
{
    public const string TargetPrefix = "fleet:";
    private const string LocalMachineName = MachineSpec.LocalName;
    private const string TerminalPresetKind = "terminal";

    private readonly IMachineCatalog _catalog;
    private readonly Func<IWorkspaceClient> _connect;
    private readonly string? _defaultCwd;
    private readonly Lock _clientGate = new();
    private readonly ConcurrentDictionary<AgentId, FleetExecutionHandle> _handles = new();
    private IWorkspaceClient? _client;
    private bool _disposed;

    public FleetAgentExecutionBackend(FleetService fleet)
        : this(
            fleet.Store,
            () => fleet.CreateClient(new FleetOptions { ClientName = "dsh-fleet-backend" }),
            fleet.Home.Root)
    {
    }

    public FleetAgentExecutionBackend(IMachineCatalog catalog, Func<IWorkspaceClient> connect, string? defaultCwd = null)
    {
        _catalog = catalog;
        _connect = connect;
        _defaultCwd = defaultCwd;
    }

    public AgentBackendKind Kind => AgentBackendKind.Fleet;

    public Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = _catalog.Load();
        IWorkspaceClient? activeClient;
        lock (_clientGate)
            activeClient = _client;

        var targets = new List<RemoteExecutionTarget>(config.Machines.Count);
        foreach (var machine in config.Machines)
        {
            var connection = activeClient?.Find(machine.Name);
            var available = connection is null || connection.State != MachineConnectionState.Offline;
            targets.Add(new RemoteExecutionTarget(
                TargetId: $"{TargetPrefix}{machine.Name}",
                Kind: AgentBackendKind.Fleet,
                DisplayName: machine.Name,
                MachineName: machine.Name,
                Endpoint: machine.Target,
                Available: available,
                Capabilities: ["fleet", machine.Kind],
                Metadata: new Dictionary<string, string>
                {
                    ["kind"] = machine.Kind,
                    ["target"] = machine.Target,
                }));
        }
        return Task.FromResult<IReadOnlyList<RemoteExecutionTarget>>(targets);
    }

    public bool CanHandle(AgentDispatchRequest request)
    {
        if (request.TargetId is { Length: > 0 } targetId)
            return targetId.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase);
        return request.PreferredBackend == AgentBackendKind.Fleet;
    }

    public async Task<IAgentExecutionHandle> DispatchAsync(
        AgentDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var machineName = ResolveMachineName(request);
        var targetId = $"{TargetPrefix}{machineName}";
        var client = await GetOrStartClientAsync(cancellationToken).ConfigureAwait(false);
        var machine = client.Find(machineName)
            ?? throw new InvalidOperationException($"Fleet machine \"{machineName}\" is not configured.");
        var lease = await machine.WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
        var (sessionId, fromSeq, isExternalAgent, initialSent) = await StartOrResumeSessionAsync(
            machine, lease.Client, request, cancellationToken).ConfigureAwait(false);
        var agentId = request.AgentId ?? AgentId.Create(sessionId);
        var hasPrompt = request.Prompt is { Length: > 0 };
        var handle = new FleetExecutionHandle(machine, agentId, SessionId.Create(sessionId), targetId, isExternalAgent, fromSeq, hasPrompt);
        _handles[agentId] = handle;

        if (!initialSent && request.Prompt is { Length: > 0 } prompt)
        {
            if (isExternalAgent)
                await lease.Client.Agent.PromptAgentAsync(sessionId, prompt, cancellationToken).ConfigureAwait(false);
            else
                await lease.Client.SendMessageAsync(sessionId, prompt, [], cancellationToken).ConfigureAwait(false);
        }

        if (request.WaitForCompletion && hasPrompt)
            await handle.WaitForTurnCompletionAsync(fromSeq, cancellationToken).ConfigureAwait(false);

        return handle;
    }

    public async Task<IAgentExecutionHandle?> AttachAsync(
        AgentId agentId,
        string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handles.TryGetValue(agentId, out var existing))
            return existing;

        var client = await GetOrStartClientAsync(cancellationToken).ConfigureAwait(false);
        var candidates = SelectCandidateMachines(client, targetId);
        foreach (var machine in candidates)
        {
            var matched = await TryAttachOnMachineAsync(machine, agentId, cancellationToken).ConfigureAwait(false);
            if (matched is not null)
            {
                _handles[agentId] = matched;
                return matched;
            }
        }
        return null;
    }

    public void Dispose()
    {
        IWorkspaceClient? client;
        lock (_clientGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            client = _client;
            _client = null;
        }
        _handles.Clear();
        if (client is not null)
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private string ResolveMachineName(AgentDispatchRequest request)
    {
        if (request.TargetId is { Length: > 0 } targetId)
        {
            if (!targetId.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Invalid Fleet target id \"{targetId}\".", nameof(request));
            var name = targetId[TargetPrefix.Length..];
            if (name.Length == 0)
                throw new ArgumentException("Fleet target id must include a machine name.", nameof(request));
            return name;
        }
        var machines = _catalog.Load().Machines;
        return machines.FirstOrDefault()?.Name
            ?? throw new InvalidOperationException("No remote machines are configured in Fleet.");
    }

    private async Task<(string SessionId, long FromSeq, bool IsExternalAgent, bool InitialSent)> StartOrResumeSessionAsync(
        IWorkspaceMachine machine,
        IWorkspaceHost host,
        AgentDispatchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SessionId is { } existingSession)
        {
            var sessionId = existingSession.Value;
            try
            {
                _ = await host.ResumeSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Already live sessions do not need resume; query snapshot next to validate existence.
            }
            var snapshot = await host.GetSessionSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return (sessionId, snapshot.TotalEventCount, false, false);
        }

        var cwd = request.Cwd ?? machine.Info?.HomeRoot ?? _defaultCwd ?? "";
        if (request.AgentPreset is { Length: > 0 } preset && host.Supports(AgentProtocol.Capability))
        {
            var isDsharp = string.Equals(preset, HostAgentKinds.Dsharp, StringComparison.Ordinal);
            if (!isDsharp && string.Equals(preset, TerminalPresetKind, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Fleet execution does not support terminal-only preset \"{preset}\".");
            var started = await host.Agent.StartAgentAsync(
                new RemoteAgentStartRequest(
                    preset,
                    cwd,
                    InitialPrompt: request.Prompt,
                    Mode: isDsharp ? null : RemoteAgentModes.Acp,
                    Title: request.Name),
                cancellationToken).ConfigureAwait(false);
            var isExternal = !string.Equals(started.AgentKind, HostAgentKinds.Dsharp, StringComparison.Ordinal);
            if (isExternal && started.Kind != HostSessionKinds.Acp)
            {
                if (started.Kind == HostSessionKinds.External)
                {
                    try
                    {
                        await host.Agent.StopAgentAsync(started.SessionId, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
                throw new NotSupportedException(
                    $"Fleet execution requires ACP mode for external preset \"{preset}\", got \"{started.Kind}\".");
            }
            return (started.SessionId, 0, isExternal, request.Prompt is { Length: > 0 });
        }

        var created = await host.CreateSessionAsync(cwd, cancellationToken).ConfigureAwait(false);
        return (created.SessionId, 0, false, false);
    }

    private IReadOnlyList<IWorkspaceMachine> SelectCandidateMachines(IWorkspaceClient client, string? targetId)
    {
        if (targetId is { Length: > 0 } && targetId.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = targetId[TargetPrefix.Length..];
            return client.Find(name) is { } found ? [found] : [];
        }
        return [.. client.Machines.Where(machine => !string.Equals(machine.Machine.Name, LocalMachineName, StringComparison.Ordinal))];
    }

    private async Task<FleetExecutionHandle?> TryAttachOnMachineAsync(
        IWorkspaceMachine machine,
        AgentId agentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var lease = await machine.WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
            var sessions = await lease.Client.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
            var match = sessions.FirstOrDefault(summary =>
                (string.Equals(summary.SessionId, agentId.Value, StringComparison.Ordinal)
                    || summary.Header.AgentId == agentId)
                && summary.Kind != HostSessionKinds.External
                && summary.Kind != HostSessionKinds.Terminal);
            if (match is null)
                return null;
            var isExternal = match.AgentKind is { Length: > 0 } kind
                && !string.Equals(kind, HostAgentKinds.Dsharp, StringComparison.Ordinal);
            var snapshot = await lease.Client.GetSessionSnapshotAsync(match.SessionId, cancellationToken).ConfigureAwait(false);
            return new FleetExecutionHandle(
                machine,
                agentId,
                SessionId.Create(match.SessionId),
                $"{TargetPrefix}{machine.Machine.Name}",
                isExternal,
                snapshot.TotalEventCount,
                awaitingTurn: false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<IWorkspaceClient> GetOrStartClientAsync(CancellationToken cancellationToken)
    {
        IWorkspaceClient client;
        lock (_clientGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is null)
            {
                client = _connect();
                client.Start();
                _client = client;
            }
            else
            {
                client = _client;
            }
        }
        await client.WaitSettledAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    private sealed class FleetExecutionHandle(
        IWorkspaceMachine machine,
        AgentId agentId,
        SessionId sessionId,
        string targetId,
        bool isExternalAgent,
        long initialSeq,
        bool awaitingTurn) : IAgentExecutionHandle
    {
        private readonly StringBuilder _turnOutput = new();
        private long _lastSeq = initialSeq;
        private bool _awaitingTurn = awaitingTurn;
        private bool _turnSettled;
        private string? _turnError;
        private AgentExecutionSnapshot _latest = new(
            agentId,
            sessionId,
            AgentBackendKind.Fleet,
            targetId,
            awaitingTurn ? AgentLifecycleState.Active : AgentLifecycleState.Idle,
            Completed: !awaitingTurn,
            UpdatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        public AgentId AgentId { get; } = agentId;
        public SessionId? SessionId { get; } = sessionId;
        public AgentBackendKind BackendKind => AgentBackendKind.Fleet;
        public string TargetId { get; } = targetId;

        public async Task<AgentExecutionSnapshot> SendAsync(
            string message,
            AgentMessageIntent intent = AgentMessageIntent.Task,
            MessagePrincipal? sender = null,
            CancellationToken cancellationToken = default)
        {
            var lease = await machine.WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
            var fromSeq = await ResolveFromSeqAsync(lease.Client, cancellationToken).ConfigureAwait(false);
            ResetTurnState(fromSeq, awaitingTurn: true);
            if (isExternalAgent)
                await lease.Client.Agent.PromptAgentAsync(sessionId.Value, message, cancellationToken).ConfigureAwait(false);
            else
                await lease.Client.SendMessageAsync(sessionId.Value, message, [], cancellationToken).ConfigureAwait(false);
            return await WaitForTurnCompletionAsync(fromSeq, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AgentExecutionSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            var lease = await machine.WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
            var sessions = await lease.Client.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
            var summary = sessions.FirstOrDefault(item => string.Equals(item.SessionId, sessionId.Value, StringComparison.Ordinal));
            await SyncEventsFromSnapshotAsync(lease.Client, cancellationToken).ConfigureAwait(false);
            if (summary is null && !_turnSettled)
                return _latest;

            var state = summary?.Status switch
            {
                HostSessionStatuses.Running => AgentLifecycleState.Active,
                HostSessionStatuses.Exited => AgentLifecycleState.Disposed,
                _ => AgentLifecycleState.Idle,
            };

            if (_awaitingTurn && !_turnSettled)
            {
                if (state == AgentLifecycleState.Disposed)
                {
                    _awaitingTurn = false;
                    _turnSettled = true;
                    _turnError ??= "remote session exited before completing turn";
                    var partial = _turnOutput.ToString();
                    _latest = new AgentExecutionSnapshot(
                        AgentId,
                        SessionId,
                        AgentBackendKind.Fleet,
                        TargetId,
                        AgentLifecycleState.Disposed,
                        Summary: partial.Length > 0 ? partial : _turnError,
                        Completed: true,
                        Output: partial.Length > 0 ? partial : null,
                        Error: _turnError,
                        UpdatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    return _latest;
                }

                _latest = _latest with
                {
                    State = AgentLifecycleState.Active,
                    Completed = false,
                    UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                return _latest;
            }

            if (_turnSettled)
            {
                _latest = _latest with
                {
                    State = state,
                    Completed = true,
                    UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                return _latest;
            }

            _latest = _latest with
            {
                State = state,
                Summary = summary?.Summary ?? _latest.Summary,
                Completed = state != AgentLifecycleState.Active,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            return _latest;
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            var lease = await machine.WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
            if (isExternalAgent && lease.Client.Supports(AgentProtocol.Capability))
                await lease.Client.Agent.CancelAgentAsync(sessionId.Value, cancellationToken).ConfigureAwait(false);
            else
                await lease.Client.InterruptAsync(sessionId.Value, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal async Task<AgentExecutionSnapshot> WaitForTurnCompletionAsync(long fromSeq, CancellationToken cancellationToken)
        {
            ResetTurnState(fromSeq, awaitingTurn: true);
            try
            {
                await foreach (var item in machine.SubscribeAsync(sessionId.Value, fromSeq, cancellationToken).ConfigureAwait(false))
                {
                    if (item.Reset)
                    {
                        _turnOutput.Clear();
                        continue;
                    }
                    if (item.Event is not { } sessionEvent)
                        continue;
                    ConsumeTurnEvent(sessionEvent);
                    if (_turnSettled)
                        return _latest;
                }
                return _latest;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    await CancelAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
                throw;
            }
        }

        private async Task SyncEventsFromSnapshotAsync(IWorkspaceHost host, CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = await host.GetSessionSnapshotAsync(sessionId.Value, cancellationToken).ConfigureAwait(false);
                foreach (var sessionEvent in snapshot.Events)
                {
                    if (sessionEvent.Seq < _lastSeq)
                        continue;
                    ConsumeTurnEvent(sessionEvent);
                    if (_turnSettled)
                        break;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
            }
        }

        private void ConsumeTurnEvent(SessionEvent sessionEvent)
        {
            _lastSeq = Math.Max(_lastSeq, sessionEvent.Seq + 1);
            if (sessionEvent.Data is AssistantMessagePayload assistant)
            {
                foreach (var block in assistant.Message.Content.OfType<TextBlock>())
                    _turnOutput.Append(block.Text);
            }
            else if (sessionEvent.Data is TurnEndPayload turnEnd)
            {
                var text = _turnOutput.ToString();
                _turnError = ExtractTurnError(turnEnd.Reason);
                _awaitingTurn = false;
                _turnSettled = true;
                _latest = new AgentExecutionSnapshot(
                    AgentId,
                    SessionId,
                    AgentBackendKind.Fleet,
                    TargetId,
                    AgentLifecycleState.Idle,
                    Summary: text.Length > 0 ? text : _turnError,
                    Completed: true,
                    Output: text,
                    Error: _turnError,
                    UpdatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }

        private void ResetTurnState(long fromSeq, bool awaitingTurn)
        {
            _lastSeq = fromSeq;
            _turnOutput.Clear();
            _turnSettled = false;
            _turnError = null;
            _awaitingTurn = awaitingTurn;
        }

        private async Task<long> ResolveFromSeqAsync(IWorkspaceHost host, CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = await host.GetSessionSnapshotAsync(sessionId.Value, cancellationToken).ConfigureAwait(false);
                _lastSeq = snapshot.TotalEventCount;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
            }
            return _lastSeq;
        }

        private static string? ExtractTurnError(TurnEndReason reason) => reason switch
        {
            TurnEndReason.Completed or TurnEndReason.MaxTokens => null,
            TurnEndReason.Error failure => failure.Failure.Message,
            TurnEndReason.Blocked => "turn was blocked",
            TurnEndReason.Interrupted or TurnEndReason.Aborted => "turn was cancelled",
            _ => $"turn ended abnormally ({reason.Kind})",
        };
    }
}
