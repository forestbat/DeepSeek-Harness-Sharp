using System.Security.Cryptography;
using System.Text;
using Dsh.Agent;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Collab;

public sealed class CollaborationRoomService : Service, ICollaborationRoomService, IDisposable
{
    private const string RoomsFileName = "rooms.json";
    private const string MessagesDirectoryName = "messages";
    private const int InviteTokenByteCount = 24;
    private static readonly TimeSpan DefaultInviteTtl = TimeSpan.FromHours(24);

    private sealed class RoomState(CollaborationRoom room, List<RoomInvite> invites, long nextSeq)
    {
        public CollaborationRoom Room { get; set; } = room;
        public List<RoomInvite> Invites { get; } = invites;
        public long NextSeq { get; set; } = nextSeq;
        public List<RoomMessage> Messages { get; } = [];
        public bool MessagesLoaded { get; set; }
    }

    private sealed record PendingAgentPrompt(
        RoomId RoomId,
        string MessageId,
        AgentId AgentId,
        string AgentDisplayName);

    private readonly TimeProvider _clock;
    private readonly string? _collabDirectory;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly Dictionary<RoomId, RoomState> _rooms = [];
    private readonly Dictionary<string, PendingAgentPrompt> _pendingPrompts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _projectedCorrelations = new(StringComparer.Ordinal);
    private readonly HashSet<AgentId> _subscribedAgents = [];
    private readonly List<IDisposable> _busSubscriptions = [];
    private IAgentMessageBus? _subscribedBus;
    private bool _disposed;

    public CollaborationRoomService(Context ctx, TimeProvider? clock = null, string? collabDirectory = null)
        : base(ctx, ICollaborationRoomService.ServiceName)
    {
        _clock = clock ?? TimeProvider.System;
        _collabDirectory = CollabStorage.ResolveCollabDirectory(ctx, collabDirectory);
        LoadFromDisk();
    }

    public async Task<CollaborationRoom> CreateRoomAsync(
        string name,
        MessagePrincipal owner,
        RoomPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.Id);
        var now = NowMs();
        var roomId = RoomId.New();
        var participant = new RoomParticipant(
            owner.Id,
            owner.Kind,
            string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.Id : owner.DisplayName,
            RoomRole.Owner,
            now,
            owner.MachineId);
        var room = new CollaborationRoom(
            roomId,
            name.Trim(),
            owner.Id,
            policy ?? new RoomPolicy(),
            [participant],
            now,
            now);
        await _persistLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CollabRoomsDocument snapshot;
            lock (_stateLock)
            {
                _rooms[roomId] = new RoomState(room, [], 1);
                snapshot = CaptureRoomsDocumentLocked();
            }
            await PersistRoomsAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return room;
        }
        finally
        {
            _persistLock.Release();
        }
    }

    public CollaborationRoom? GetRoom(RoomId roomId)
    {
        lock (_stateLock)
            return _rooms.TryGetValue(roomId, out var state) ? state.Room : null;
    }

    public IReadOnlyList<CollaborationRoom> ListRooms()
    {
        lock (_stateLock)
            return _rooms.Values.Select(entry => entry.Room).OrderBy(room => room.CreatedAt).ToList();
    }

    public async Task<RoomInvite> CreateInviteAsync(
        RoomId roomId,
        MessagePrincipal actor,
        RoomRole role,
        TimeSpan? ttl = null,
        int maxUses = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.Id);
        if (maxUses < 1)
            throw new ArgumentOutOfRangeException(nameof(maxUses), "maxUses must be at least 1.");
        var effectiveTtl = ttl ?? DefaultInviteTtl;
        var now = NowMs();
        var expiresAt = now + (long)effectiveTtl.TotalMilliseconds;
        var invite = new RoomInvite(
            $"inv-{Guid.NewGuid():N}",
            roomId,
            GenerateInviteToken(),
            role,
            actor.Id,
            now,
            expiresAt,
            maxUses);

        await _persistLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CollabRoomsDocument snapshot;
            lock (_stateLock)
            {
                var state = RequireRoomLocked(roomId);
                AssertCanPerformLocked(state.Room, actor.Id, RoomActions.Invite);
                state.Invites.Add(invite);
                snapshot = CaptureRoomsDocumentLocked();
            }
            await PersistRoomsAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return invite;
        }
        finally
        {
            _persistLock.Release();
        }
    }

    public async Task<CollaborationRoom> RedeemInviteAsync(
        string token,
        MessagePrincipal joiner,
        AgentId? agentId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(joiner.Id);
        var now = NowMs();

        await _persistLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CollabRoomsDocument snapshot;
            CollaborationRoom updatedRoom;
            lock (_stateLock)
            {
                var (state, inviteIndex) = FindInviteByTokenLocked(token);
                var invite = state.Invites[inviteIndex];
                if (invite.Revoked)
                    throw new InvalidOperationException("Invite has been revoked.");
                if (now >= invite.ExpiresAt)
                    throw new InvalidOperationException("Invite has expired.");
                if (invite.UsedCount >= invite.MaxUses)
                    throw new InvalidOperationException("Invite has reached its maximum uses.");

                state.Invites[inviteIndex] = invite with { UsedCount = invite.UsedCount + 1 };
                var resolvedRole = invite.Role;
                var participant = new RoomParticipant(
                    joiner.Id,
                    joiner.Kind,
                    string.IsNullOrWhiteSpace(joiner.DisplayName) ? joiner.Id : joiner.DisplayName,
                    resolvedRole,
                    now,
                    joiner.MachineId,
                    agentId);
                updatedRoom = UpsertParticipant(state.Room, participant, now);
                state.Room = updatedRoom;
                snapshot = CaptureRoomsDocumentLocked();
            }
            await PersistRoomsAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return updatedRoom;
        }
        finally
        {
            _persistLock.Release();
        }
    }

    public async Task<bool> RevokeInviteAsync(
        RoomId roomId,
        string inviteId,
        MessagePrincipal actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.Id);

        await _persistLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CollabRoomsDocument? snapshot = null;
            var revoked = false;
            lock (_stateLock)
            {
                var state = RequireRoomLocked(roomId);
                AssertCanPerformLocked(state.Room, actor.Id, RoomActions.Invite);
                var index = state.Invites.FindIndex(item => string.Equals(item.InviteId, inviteId, StringComparison.Ordinal));
                if (index >= 0)
                {
                    state.Invites[index] = state.Invites[index] with { Revoked = true };
                    snapshot = CaptureRoomsDocumentLocked();
                    revoked = true;
                }
            }
            if (snapshot is not null)
                await PersistRoomsAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return revoked;
        }
        finally
        {
            _persistLock.Release();
        }
    }

    public async Task<CollaborationRoom> AddAgentAsync(
        RoomId roomId,
        MessagePrincipal actor,
        AgentId agentId,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId.Value);
        var now = NowMs();

        CollaborationRoom updatedRoom;
        await _persistLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CollabRoomsDocument snapshot;
            lock (_stateLock)
            {
                var state = RequireRoomLocked(roomId);
                AssertCanPerformLocked(state.Room, actor.Id, RoomActions.ManageMembers);
                var participant = new RoomParticipant(
                    agentId.Value,
                    PrincipalKind.Agent,
                    string.IsNullOrWhiteSpace(displayName) ? agentId.Value : displayName.Trim(),
                    RoomRole.AgentParticipant,
                    now,
                    AgentId: agentId);
                updatedRoom = UpsertParticipant(state.Room, participant, now);
                state.Room = updatedRoom;
                snapshot = CaptureRoomsDocumentLocked();
            }
            await PersistRoomsAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _persistLock.Release();
        }

        EnsureBusSubscription(agentId);
        return updatedRoom;
    }

    public async Task<RoomMessage> PostMessageAsync(
        RoomId roomId,
        MessagePrincipal sender,
        string body,
        AgentId? targetAgentId = null,
        string? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sender.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        var now = NowMs();

        RoomMessage message;
        CollaborationRoom roomSnapshot;
        AgentId? resolvedTargetAgent;
        RoomRole senderRole;

        await _persistLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CollabRoomsDocument roomsSnapshot;
            CollabRoomMessagesDocument messagesSnapshot;
            lock (_stateLock)
            {
                var state = RequireRoomLocked(roomId);
                EnsureMessagesLoadedLocked(state);
                AssertCanPerformLocked(state.Room, sender.Id, RoomActions.Post);
                senderRole = ResolveRoleLocked(state.Room, sender.Id)
                    ?? throw new UnauthorizedAccessException($"Principal '{sender.Id}' is not a participant of room '{roomId.Value}'.");
                resolvedTargetAgent = ResolveTargetAgent(state.Room, body, targetAgentId);
                if (resolvedTargetAgent is not null && sender.Kind != PrincipalKind.Agent)
                    AssertCanPerformLocked(state.Room, sender.Id, RoomActions.PromptAgent);

                message = new RoomMessage(
                    $"rmsg-{Guid.NewGuid():N}",
                    roomId,
                    state.NextSeq++,
                    sender.Id,
                    sender.Kind,
                    string.IsNullOrWhiteSpace(sender.DisplayName) ? sender.Id : sender.DisplayName,
                    body,
                    now,
                    resolvedTargetAgent,
                    replyToMessageId);
                state.Messages.Add(message);
                state.Room = state.Room with { UpdatedAt = now };
                roomSnapshot = state.Room;
                roomsSnapshot = CaptureRoomsDocumentLocked();
                messagesSnapshot = new CollabRoomMessagesDocument(roomId, [.. state.Messages]);
            }
            await PersistRoomsAsync(roomsSnapshot, cancellationToken).ConfigureAwait(false);
            await PersistMessagesAsync(messagesSnapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _persistLock.Release();
        }

        if (resolvedTargetAgent is { } agentTarget && sender.Kind != PrincipalKind.Agent)
            await DispatchAgentPromptAsync(roomSnapshot, message, senderRole, agentTarget, cancellationToken).ConfigureAwait(false);

        return message;
    }

    public IReadOnlyList<RoomMessage> ReadMessages(RoomId roomId, long afterSeq = 0, int limit = 100)
    {
        if (limit <= 0)
            return [];
        lock (_stateLock)
        {
            if (!_rooms.TryGetValue(roomId, out var state))
                return [];
            EnsureMessagesLoadedLocked(state);
            return state.Messages
                .Where(msg => msg.Seq > afterSeq)
                .OrderBy(msg => msg.Seq)
                .Take(limit)
                .ToList();
        }
    }

    public bool CanPerform(RoomId roomId, string principalId, string action)
    {
        if (string.IsNullOrWhiteSpace(principalId) || string.IsNullOrWhiteSpace(action))
            return false;
        lock (_stateLock)
        {
            if (!_rooms.TryGetValue(roomId, out var state))
                return false;
            return CanPerformLocked(state.Room, principalId, action);
        }
    }

    public IReadOnlyList<RoomInvite> ListInvites(RoomId roomId)
    {
        lock (_stateLock)
            return _rooms.TryGetValue(roomId, out var state) ? state.Invites.ToList() : [];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        List<IDisposable> subscriptions;
        lock (_stateLock)
        {
            subscriptions = [.. _busSubscriptions];
            _busSubscriptions.Clear();
            _subscribedAgents.Clear();
            _subscribedBus = null;
        }
        foreach (var subscription in subscriptions)
            subscription.Dispose();
        _persistLock.Dispose();
    }

    private async Task DispatchAgentPromptAsync(
        CollaborationRoom room,
        RoomMessage roomMessage,
        RoomRole senderRole,
        AgentId targetAgentId,
        CancellationToken cancellationToken)
    {
        var bus = Ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false);
        if (bus is null)
            return;

        EnsureBusSubscription(targetAgentId, bus);

        var correlationId = $"room:{room.Id.Value}:{roomMessage.MessageId}";
        var agentParticipant = room.Participants.FirstOrDefault(p => p.AgentId == targetAgentId || p.PrincipalId == targetAgentId.Value);
        var agentDisplayName = agentParticipant?.DisplayName ?? targetAgentId.Value;

        lock (_stateLock)
            _pendingPrompts[correlationId] = new PendingAgentPrompt(room.Id, roomMessage.MessageId, targetAgentId, agentDisplayName);

        var trustLevel = roomMessage.SenderKind == PrincipalKind.ExternalChannel
            ? MessageTrustLevel.ExternalAgent
            : senderRole == RoomRole.Owner
                ? MessageTrustLevel.OwnerHuman
                : MessageTrustLevel.DelegatedHuman;
        var senderKind = roomMessage.SenderKind switch
        {
            PrincipalKind.RemoteUser => PrincipalKind.RemoteUser,
            PrincipalKind.ExternalChannel => PrincipalKind.ExternalChannel,
            _ => PrincipalKind.Human,
        };
        var sender = new MessagePrincipal(
            senderKind,
            roomMessage.SenderPrincipalId,
            roomMessage.SenderDisplayName,
            ChannelId: room.Id.Value);
        var promptBody = $"[Room: {room.Name} ({room.Id.Value})] {roomMessage.SenderDisplayName}: {roomMessage.Body}";
        var envelope = new AgentEnvelope(
            $"env-{Guid.NewGuid():N}",
            targetAgentId,
            null,
            sender,
            trustLevel,
            AgentMessageIntent.Task,
            promptBody,
            correlationId,
            correlationId,
            roomMessage.Timestamp,
            WakeIfSuspended: true,
            new Dictionary<string, string>
            {
                ["roomId"] = room.Id.Value,
                ["roomMessageId"] = roomMessage.MessageId,
                ["roomSenderId"] = roomMessage.SenderPrincipalId,
            });

        var result = await bus.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (result.Status == AgentDeliveryStatus.Rejected)
        {
            lock (_stateLock)
                _pendingPrompts.Remove(correlationId);
        }
    }

    private void EnsureBusSubscription(AgentId agentId, IAgentMessageBus? resolvedBus = null)
    {
        var bus = resolvedBus ?? Ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false);
        if (bus is null)
            return;

        var subscribeWildcard = false;
        var subscribeAgent = false;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_subscribedBus, bus))
            {
                foreach (var old in _busSubscriptions)
                    old.Dispose();
                _busSubscriptions.Clear();
                _subscribedAgents.Clear();
                _subscribedBus = bus;
                subscribeWildcard = true;
            }
            if (_subscribedAgents.Add(agentId))
                subscribeAgent = true;
        }

        if (subscribeWildcard)
        {
            var wildcard = new AgentEventSubscription(
                $"collab-rooms-wildcard-{Guid.NewGuid():N}",
                default,
                null,
                [AgentRuntimeEventKind.TurnCompleted],
                WakeSubscriber: false);
            var handle = bus.Subscribe(wildcard, OnAgentRuntimeEventAsync);
            lock (_stateLock)
                _busSubscriptions.Add(handle);
        }

        if (subscribeAgent)
        {
            var perAgent = new AgentEventSubscription(
                $"collab-rooms-{agentId.Value}-{Guid.NewGuid():N}",
                agentId,
                null,
                [AgentRuntimeEventKind.TurnCompleted],
                WakeSubscriber: false);
            var handle = bus.Subscribe(perAgent, OnAgentRuntimeEventAsync);
            lock (_stateLock)
                _busSubscriptions.Add(handle);
        }
    }

    private async ValueTask OnAgentRuntimeEventAsync(AgentRuntimeEvent runtimeEvent)
    {
        if (_disposed || runtimeEvent.Kind != AgentRuntimeEventKind.TurnCompleted)
            return;
        if (string.IsNullOrWhiteSpace(runtimeEvent.CorrelationId))
            return;

        PendingAgentPrompt? pending;
        lock (_stateLock)
        {
            if (!_pendingPrompts.TryGetValue(runtimeEvent.CorrelationId, out pending))
                return;
            if (!_projectedCorrelations.Add(runtimeEvent.CorrelationId))
                return;
            _pendingPrompts.Remove(runtimeEvent.CorrelationId);
        }

        var replyText = ExtractReplyText(runtimeEvent);
        if (string.IsNullOrWhiteSpace(replyText))
            return;

        var agentPrincipal = new MessagePrincipal(
            PrincipalKind.Agent,
            pending.AgentId.Value,
            pending.AgentDisplayName);
        await PostMessageAsync(
            pending.RoomId,
            agentPrincipal,
            replyText,
            replyToMessageId: pending.MessageId).ConfigureAwait(false);
    }

    private string? ExtractReplyText(AgentRuntimeEvent runtimeEvent)
    {
        if (!string.IsNullOrWhiteSpace(runtimeEvent.Detail))
            return runtimeEvent.Detail;

        var registry = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        if (registry is not null)
        {
            var agent = runtimeEvent.SessionId is { } sid
                ? registry.Get(sid)
                : registry.List().FirstOrDefault(candidate => candidate.AgentId == runtimeEvent.AgentId);
            if (agent is not null)
            {
                var assistant = agent.Session.SnapshotEvents()
                    .Select(ev => ev.Data)
                    .OfType<AssistantMessagePayload>()
                    .LastOrDefault();
                if (assistant is not null)
                {
                    var text = MessageText.Flatten(assistant.Message.Content);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
        }

        return string.IsNullOrWhiteSpace(runtimeEvent.Summary) ? null : runtimeEvent.Summary;
    }

    private static AgentId? ResolveTargetAgent(CollaborationRoom room, string body, AgentId? explicitTarget)
    {
        if (explicitTarget is { } target)
            return target;

        var agentParticipants = room.Participants
            .Where(p => p.Role == RoomRole.AgentParticipant || p.AgentId is not null)
            .ToList();
        if (agentParticipants.Count == 0)
            return null;

        RoomParticipant? bestMatch = null;
        var bestMatchLength = 0;
        foreach (var participant in agentParticipants)
        {
            var candidates = new[] { participant.AgentId?.Value, participant.PrincipalId, participant.DisplayName };
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;
                if (candidate.Length > bestMatchLength && ContainsMention(body, candidate))
                {
                    bestMatch = participant;
                    bestMatchLength = candidate.Length;
                }
            }
        }

        if (bestMatch is not null)
            return bestMatch.AgentId ?? AgentId.Create(bestMatch.PrincipalId);

        if (!room.Policy.RequireExplicitAgentMention)
            return agentParticipants[0].AgentId ?? AgentId.Create(agentParticipants[0].PrincipalId);

        return null;
    }

    private static bool ContainsMention(string body, string handle)
    {
        var search = $"@{handle}";
        var startIndex = 0;
        while (startIndex < body.Length)
        {
            var found = body.IndexOf(search, startIndex, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return false;
            var beforeOk = found == 0 || !IsHandleChar(body[found - 1]);
            var afterIndex = found + search.Length;
            var afterOk = afterIndex >= body.Length || !IsHandleChar(body[afterIndex]);
            if (beforeOk && afterOk)
                return true;
            startIndex = found + 1;
        }
        return false;
    }

    private static bool IsHandleChar(char ch) => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.';

    private static CollaborationRoom UpsertParticipant(CollaborationRoom room, RoomParticipant participant, long now)
    {
        var updated = room.Participants
            .Where(existing => !string.Equals(existing.PrincipalId, participant.PrincipalId, StringComparison.Ordinal))
            .ToList();
        updated.Add(participant);
        return room with { Participants = updated, UpdatedAt = now };
    }

    private static RoomRole? ResolveRoleLocked(CollaborationRoom room, string principalId)
    {
        if (string.Equals(room.OwnerPrincipalId, principalId, StringComparison.Ordinal))
            return RoomRole.Owner;
        var participant = room.Participants.FirstOrDefault(p =>
            string.Equals(p.PrincipalId, principalId, StringComparison.Ordinal)
            || (p.AgentId is { } aid && string.Equals(aid.Value, principalId, StringComparison.Ordinal)));
        return participant?.Role;
    }

    private static bool CanPerformLocked(CollaborationRoom room, string principalId, string action)
    {
        if (ResolveRoleLocked(room, principalId) is not { } role)
            return false;
        return role switch
        {
            RoomRole.Owner => action is RoomActions.Read or RoomActions.Post or RoomActions.PromptAgent or RoomActions.Approve or RoomActions.Invite or RoomActions.ManageMembers,
            RoomRole.Approver => action is RoomActions.Read or RoomActions.Post or RoomActions.PromptAgent or RoomActions.Approve,
            RoomRole.Collaborator => action switch
            {
                RoomActions.Read or RoomActions.Post => true,
                RoomActions.PromptAgent => room.Policy.AllowCollaboratorPrompts,
                RoomActions.Approve => room.Policy.AllowCollaboratorApprovals,
                _ => false,
            },
            RoomRole.Observer => action == RoomActions.Read && room.Policy.AllowObserverRead,
            RoomRole.AgentParticipant => action is RoomActions.Read or RoomActions.Post,
            _ => false,
        };
    }

    private static void AssertCanPerformLocked(CollaborationRoom room, string principalId, string action)
    {
        if (!CanPerformLocked(room, principalId, action))
            throw new UnauthorizedAccessException($"Principal '{principalId}' is not permitted to perform '{action}' in room '{room.Id.Value}'.");
    }

    private RoomState RequireRoomLocked(RoomId roomId)
        => _rooms.TryGetValue(roomId, out var state)
            ? state
            : throw new InvalidOperationException($"Collaboration room '{roomId.Value}' not found.");

    private (RoomState State, int InviteIndex) FindInviteByTokenLocked(string token)
    {
        var candidateBytes = Encoding.UTF8.GetBytes(token);
        foreach (var state in _rooms.Values)
        {
            for (var index = 0; index < state.Invites.Count; index++)
            {
                var expectedBytes = Encoding.UTF8.GetBytes(state.Invites[index].Token);
                if (expectedBytes.Length == candidateBytes.Length
                    && CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes))
                    return (state, index);
            }
        }
        throw new InvalidOperationException("Invite token is invalid.");
    }

    private static string GenerateInviteToken()
    {
        Span<byte> buffer = stackalloc byte[InviteTokenByteCount];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private CollabRoomsDocument CaptureRoomsDocumentLocked()
        => new([.. _rooms.Values.Select(entry => new CollabRoomEntry(entry.Room, [.. entry.Invites], entry.NextSeq))]);

    private void LoadFromDisk()
    {
        if (RoomsFilePath() is not { } roomsPath)
            return;
        var doc = CollabStorage.ReadJson(roomsPath, DshCollabJsonContext.Default.CollabRoomsDocument);
        if (doc is null)
            return;
        lock (_stateLock)
        {
            foreach (var entry in doc.Rooms)
                _rooms[entry.Room.Id] = new RoomState(entry.Room, [.. entry.Invites], Math.Max(1, entry.NextSeq));
        }
    }

    private void EnsureMessagesLoadedLocked(RoomState state)
    {
        if (state.MessagesLoaded)
            return;
        state.MessagesLoaded = true;
        if (MessagesFilePath(state.Room.Id) is not { } path)
            return;
        var doc = CollabStorage.ReadJson(path, DshCollabJsonContext.Default.CollabRoomMessagesDocument);
        if (doc is null)
            return;
        state.Messages.AddRange(doc.Messages.OrderBy(msg => msg.Seq));
        if (state.Messages.Count > 0)
            state.NextSeq = Math.Max(state.NextSeq, state.Messages[^1].Seq + 1);
    }

    private async Task PersistRoomsAsync(CollabRoomsDocument document, CancellationToken cancellationToken)
    {
        if (RoomsFilePath() is { } path)
            await CollabStorage.WriteJsonAtomicAsync(path, document, DshCollabJsonContext.Default.CollabRoomsDocument, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistMessagesAsync(CollabRoomMessagesDocument document, CancellationToken cancellationToken)
    {
        if (MessagesFilePath(document.RoomId) is { } path)
            await CollabStorage.WriteJsonAtomicAsync(path, document, DshCollabJsonContext.Default.CollabRoomMessagesDocument, cancellationToken).ConfigureAwait(false);
    }

    private string? RoomsFilePath()
        => _collabDirectory is null ? null : Path.Combine(_collabDirectory, RoomsFileName);

    private string? MessagesFilePath(RoomId roomId)
        => _collabDirectory is null
            ? null
            : Path.Combine(_collabDirectory, MessagesDirectoryName, $"{CollabStorage.SanitizeFileSegment(roomId.Value)}.json");

    private long NowMs() => _clock.GetUtcNow().ToUnixTimeMilliseconds();
}
