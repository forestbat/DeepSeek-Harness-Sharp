using System.Text.Json;
using Dsh.Acp;
using Dsh.Core;
using Dsh.AgentAdapters;
using Dsh.AgentAdapters.Acp;
using Dsh.Interaction;
using Dsh.Notifications;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Host;

/** What an ACP session needs from the host: the registry, the pending queue for permission requests, and the notification history. */
internal sealed record AcpSessionServices(HostSessionRegistry Registry, HostPendingDecisions Pending, HostNotificationCenter Notifications);

/**
 * One external agent driven over ACP and owned by the Host; the process outlives client connections.
 * Turns run in the background and updates feed the shared transcript.
 * Pending decisions are shared across clients with first-winner semantics and only the provider's once options are selected.
 * Remembered approvals reuse the existing logical Session journal for an exact stable tool name.
 * A cancelled request answers "cancelled", and persisted history remains readable after the process exits.
 */
internal sealed class AcpAgentSession : IAsyncDisposable
{
    /** initialize and session/new of a cold agent take seconds (OpenCode starts an internal server first). */
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(90);

    /** Error code on turn/end when session/prompt failed instead of returning a stop reason. */
    private const string PromptFailedCode = "acp_prompt_failed";

    private readonly AcpAgentProcess _process;
    private readonly AcpTranscript _transcript;
    private readonly AcpSessionServices _services;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _turnGate = new();
    private Task _turn = Task.CompletedTask;

    private AcpAgentSession(string sessionId, string acpSessionId, AcpAgentProcess process, AcpTranscript transcript, AcpSessionServices services)
    {
        SessionId = sessionId;
        AcpSessionId = acpSessionId;
        _process = process;
        _transcript = transcript;
        _services = services;
        process.Client.SessionUpdate += OnUpdate;
        _ = PumpPermissionsAsync();
    }

    public string SessionId { get; }

    /** The agent's own session id, kept for session/resume after the process ends. */
    public string AcpSessionId { get; }

    /** Completes with the exit code once the agent process is gone. */
    public Task<int> Exited => _process.Exited;

    /**
     * Starts the agent process and opens a new ACP session, or reopens ResumeId (session/resume when supported, else session/load,
     * whose history replay is not recorded again because the transcript already holds it).
     */
    public static async Task<AcpAgentSession> StartAsync(
        RemoteSessionSummary registration,
        AgentCommand command,
        IReadOnlyDictionary<string, string>? environment,
        string? resumeId,
        Context ctx,
        AcpSessionServices services,
        CancellationToken cancellationToken)
    {
        var workingDirectory = registration.ProjectPath!;
        var process = AcpAgentProcess.Start(command, workingDirectory, environment);
        try
        {
            var client = process.Client;
            var info = await client.InitializeAsync(new AcpImplementationInfo("dsharp-host", HostProtocol.ReleaseVersion), cancellationToken).WaitAsync(HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            var opened = resumeId is null
                ? client.NewSessionAsync(workingDirectory, cancellationToken)
                : info.CanResumeSession ? client.ResumeSessionAsync(resumeId, workingDirectory, cancellationToken)
                : info.CanLoadSession ? client.LoadSessionAsync(resumeId, workingDirectory, cancellationToken)
                : throw new NotSupportedException($"{command.Executable} can neither resume nor load ACP sessions");
            var session = await opened.WaitAsync(HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            var acpSessionId = session.SessionId ?? resumeId ?? throw new InvalidOperationException("the agent returned no session id");
            var transcript = await AcpTranscript.OpenAsync(ctx, registration).ConfigureAwait(false);
            return new AcpAgentSession(registration.SessionId, acpSessionId, process, transcript, services);
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /** Hands one user turn to the agent; throws when a turn is still running. */
    public void Prompt(string text)
    {
        lock (_turnGate)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!_turn.IsCompleted)
                throw new InvalidOperationException($"session {SessionId} is still running a turn; cancel it first");
            _transcript.BeginTurn(text);
            _services.Registry.SetLiveStatus(SessionId, HostSessionStatuses.Running);
            _turn = RunTurnAsync(text);
        }
    }

    /** session/cancel: the agent ends the turn with stopReason "cancelled"; its open permission requests are answered "cancelled". */
    public Task CancelAsync(CancellationToken cancellationToken) => _process.Client.CancelAsync(AcpSessionId, cancellationToken);

    private async Task RunTurnAsync(string text)
    {
        try
        {
            _transcript.EndTurn(await _process.Client.PromptAsync(AcpSessionId, text, _lifetime.Token).ConfigureAwait(false));
        }
        catch (Exception error)
        {
            if (_lifetime.IsCancellationRequested)
                return;
            _transcript.FailTurn(error.Message, PromptFailedCode);
            _services.Notifications.Report(NotificationKind.Error, SessionId, $"{_services.Notifications.Label(SessionId)} failed", error.Message);
        }
        _services.Registry.SetLiveStatus(SessionId, HostSessionStatuses.Idle);
    }

    private void OnUpdate(AcpSessionUpdate update)
    {
        if (update.SessionId == AcpSessionId)
            _transcript.Apply(update);
    }

    private async Task PumpPermissionsAsync()
    {
        try
        {
            await foreach (var permission in _process.Client.PermissionRequests.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                _ = BridgePermissionAsync(permission);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /** The request is scoped to its own id, so a request the agent withdrew (turn cancelled, agent gone) is cancelled for every client. */
    private async Task BridgePermissionAsync(AcpPermissionRequest permission)
    {
        if (permission.IsResolved)
            return;
        var toolCall = permission.ToolCall;
        var arguments = toolCall.Raw.ValueKind == JsonValueKind.Object && toolCall.Raw.TryGetProperty("rawInput", out var input) ? input.GetRawText() : null;
        var stableName = toolCall.Raw.ValueKind == JsonValueKind.Object && toolCall.Raw.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString() : null;
        if (string.IsNullOrWhiteSpace(stableName))
            stableName = null;
        var (allowOnce, rejectOnce) = OnceOptions(permission.Options);
        if (_transcript.TryCompletePermissionWithoutPrompt(permission, stableName, arguments, allowOnce))
            return;
        var toolName = stableName ?? toolCall.Kind ?? toolCall.Title ?? "tool";
        var request = new RemoteApprovalRequest(permission.RequestId, SessionId, toolName, arguments, toolCall.Title,
            ApprovalHints.Impact(toolName, arguments), new RemoteApprovalAvailability(allowOnce is not null,
                allowOnce is not null && stableName is not null, rejectOnce is not null));
        var answer = _services.Pending.AskApprovalAsync(request, permission.RequestId);
        if (await Task.WhenAny(answer, permission.Outcome).ConfigureAwait(false) != answer)
        {
            _services.Pending.CancelScope(permission.RequestId);
            return;
        }
        var response = await answer.ConfigureAwait(false);
        var outcome = response switch
        {
            null or { Cancelled: true } => ApprovalOutcome.Cancelled,
            { Allow: true, RememberForSession: true } => ApprovalOutcome.AllowedForSession,
            { Allow: true } => ApprovalOutcome.AllowedOnce,
            _ => ApprovalOutcome.Rejected,
        };
        var option = outcome switch
        {
            ApprovalOutcome.AllowedOnce or ApprovalOutcome.AllowedForSession => allowOnce,
            ApprovalOutcome.Rejected => rejectOnce,
            _ => null,
        };
        _transcript.CompletePermission(permission, toolName, arguments, option, outcome);
    }

    private static (AcpPermissionOption? Allow, AcpPermissionOption? Reject) OnceOptions(IReadOnlyList<AcpPermissionOption> options)
    {
        AcpPermissionOption? allow = null;
        AcpPermissionOption? reject = null;
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            if (option.Kind == AcpMethods.PermissionAllowOnce)
                allow ??= option;
            else if (option.Kind == AcpMethods.PermissionRejectOnce)
                reject ??= option;
            if (allow is not null && reject is not null)
                break;
        }
        return (allow, reject);
    }

    /** Ends the agent process: stdin closed, then killed after a grace period. */
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task turn;
        lock (_turnGate)
            turn = _turn;
        _process.Client.SessionUpdate -= OnUpdate;
        await _process.DisposeAsync().ConfigureAwait(false);
        await turn.ConfigureAwait(false);
        await _transcript.DisposeAsync().ConfigureAwait(false);
        _services.Registry.MarkExited(SessionId, await Exited.ConfigureAwait(false));
        _lifetime.Dispose();
    }
}


