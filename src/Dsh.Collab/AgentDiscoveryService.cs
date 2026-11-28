using System.Text.Json;
using Dsh.Agent;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Collab;

public sealed class AgentDiscoveryService : Service, IAgentDiscoveryService, IDisposable
{
    private const string TrustFileName = "trust.json";

    public static readonly AgentTrustPermissions NoPermissions = new(
        AllowMessaging: false,
        AllowTaskDelegation: false,
        AllowEventSubscription: false,
        AllowRoomParticipation: false,
        AllowFileAccess: false,
        AllowToolControl: false);

    private readonly TimeProvider _clock;
    private readonly string? _collabDirectory;
    private readonly Lock _gate = new();
    private readonly List<IAgentDiscoveryProvider> _providers = [];
    private readonly Dictionary<string, DiscoveredAgentCard> _knownCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiscoveredAgentCard> _importedCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentTrustRecord> _trustRecords = new(StringComparer.Ordinal);

    public AgentDiscoveryService(Context ctx, TimeProvider? clock = null, string? collabDirectory = null)
        : base(ctx, IAgentDiscoveryService.ServiceName)
    {
        _clock = clock ?? TimeProvider.System;
        _collabDirectory = CollabStorage.ResolveCollabDirectory(ctx, collabDirectory);
        LoadFromDisk();
    }

    public IDisposable RegisterProvider(IAgentDiscoveryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
            _providers.Add(provider);
        return new ProviderRegistration(() =>
        {
            lock (_gate)
                _providers.Remove(provider);
        });
    }

    public async Task<IReadOnlyList<DiscoveredAgentCard>> DiscoverAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        List<IAgentDiscoveryProvider> providersSnapshot;
        List<DiscoveredAgentCard> importedSnapshot;
        lock (_gate)
        {
            providersSnapshot = [.. _providers];
            importedSnapshot = [.. _importedCards.Values];
        }

        var discovered = new Dictionary<string, DiscoveredAgentCard>(StringComparer.Ordinal);
        foreach (var card in await CollectBuiltInCardsAsync(cancellationToken).ConfigureAwait(false))
            discovered.TryAdd(card.CardId, card);

        foreach (var provider in providersSnapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cards = await provider.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            foreach (var card in cards)
                discovered.TryAdd(card.CardId, card);
        }

        foreach (var card in importedSnapshot)
            discovered.TryAdd(card.CardId, card);

        var now = NowMs();
        AgentTrustStoreDocument? updatedDoc = null;
        lock (_gate)
        {
            var dirty = false;
            foreach (var card in discovered.Values)
            {
                _knownCards[card.CardId] = card;
                if (!_trustRecords.ContainsKey(card.CardId))
                {
                    _trustRecords[card.CardId] = new AgentTrustRecord(
                        card.CardId,
                        card.Name,
                        card.Endpoint,
                        AgentTrustStatus.Discovered,
                        NoPermissions,
                        "system:discovery",
                        now,
                        "Discovered; explicit approval required");
                    dirty = true;
                }
            }
            if (dirty)
                updatedDoc = CaptureDocumentLocked();
        }
        if (updatedDoc is not null)
            await PersistTrustAsync(updatedDoc, cancellationToken).ConfigureAwait(false);

        return discovered.Values
            .Where(card => MatchesQuery(card, query))
            .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(card => card.CardId, StringComparer.Ordinal)
            .ToList();
    }

    public DiscoveredAgentCard? ImportCard(string cardJson, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(cardJson))
            return null;
        var parsed = JsonSerializer.Deserialize(cardJson, DshCollabJsonContext.Default.DiscoveredAgentCard);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.CardId) || string.IsNullOrWhiteSpace(parsed.Name))
            return null;

        var now = NowMs();
        var normalized = parsed with
        {
            SourceProvider = string.IsNullOrWhiteSpace(source) ? (parsed.SourceProvider ?? "imported") : source,
            DiscoveredAt = parsed.DiscoveredAt > 0 ? parsed.DiscoveredAt : now,
        };

        AgentTrustStoreDocument snapshot;
        lock (_gate)
        {
            _importedCards[normalized.CardId] = normalized;
            _knownCards[normalized.CardId] = normalized;
            // Discovery or card import never grants permissions; explicit GrantTrust is required.
            if (!_trustRecords.TryGetValue(normalized.CardId, out var existing))
            {
                _trustRecords[normalized.CardId] = new AgentTrustRecord(
                    normalized.CardId,
                    normalized.Name,
                    normalized.Endpoint,
                    AgentTrustStatus.PendingApproval,
                    NoPermissions,
                    "system:import",
                    now,
                    "Imported card awaiting explicit trust approval");
            }
            else if (existing.Status != AgentTrustStatus.Trusted)
            {
                _trustRecords[normalized.CardId] = existing with
                {
                    Name = normalized.Name,
                    Endpoint = normalized.Endpoint,
                    Permissions = NoPermissions,
                };
            }
            snapshot = CaptureDocumentLocked();
        }
        PersistTrustSync(snapshot);
        return normalized;
    }

    public string ExportLocalCardJson(AgentId agentId)
    {
        var profile = ResolveLocalProfile(agentId)
            ?? throw new InvalidOperationException($"Local agent '{agentId.Value}' not found.");
        var card = CardFromProfile(profile, NowMs(), "local-export");
        return JsonSerializer.Serialize(card, DshCollabJsonContext.Default.DiscoveredAgentCard);
    }

    public IReadOnlyList<AgentTrustRecord> ListTrust()
    {
        lock (_gate)
            return _trustRecords.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public AgentTrustRecord? GetTrust(string cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            return null;
        lock (_gate)
            return _trustRecords.TryGetValue(cardId, out var record) ? record : null;
    }

    public AgentTrustRecord GrantTrust(
        string cardId,
        AgentTrustPermissions permissions,
        string actorPrincipalId,
        string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorPrincipalId);
        ArgumentNullException.ThrowIfNull(permissions);
        var now = NowMs();

        AgentTrustRecord updated;
        AgentTrustStoreDocument snapshot;
        lock (_gate)
        {
            var (name, endpoint) = ResolveCardIdentityLocked(cardId);
            updated = new AgentTrustRecord(
                cardId,
                name,
                endpoint,
                AgentTrustStatus.Trusted,
                permissions,
                actorPrincipalId,
                now,
                note);
            _trustRecords[cardId] = updated;
            snapshot = CaptureDocumentLocked();
        }
        PersistTrustSync(snapshot);
        return updated;
    }

    public AgentTrustRecord RevokeTrust(string cardId, string actorPrincipalId, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorPrincipalId);
        var now = NowMs();

        AgentTrustRecord revoked;
        AgentTrustStoreDocument snapshot;
        lock (_gate)
        {
            var (name, endpoint) = ResolveCardIdentityLocked(cardId);
            revoked = new AgentTrustRecord(
                cardId,
                name,
                endpoint,
                AgentTrustStatus.Revoked,
                NoPermissions,
                actorPrincipalId,
                now,
                reason ?? "Trust revoked");
            _trustRecords[cardId] = revoked;
            snapshot = CaptureDocumentLocked();
        }
        PersistTrustSync(snapshot);
        return revoked;
    }

    public bool IsTrustedFor(string cardId, Func<AgentTrustPermissions, bool> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (string.IsNullOrWhiteSpace(cardId))
            return false;
        lock (_gate)
        {
            return _trustRecords.TryGetValue(cardId, out var record)
                && record.Status == AgentTrustStatus.Trusted
                && check(record.Permissions);
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _providers.Clear();
    }

    private async Task<IReadOnlyList<DiscoveredAgentCard>> CollectBuiltInCardsAsync(CancellationToken cancellationToken)
    {
        var now = NowMs();
        var cards = new Dictionary<string, DiscoveredAgentCard>(StringComparer.Ordinal);

        if (Ctx.Get<IAgentDirectoryStore>(IAgentDirectoryStore.ServiceName, false) is { } directory)
        {
            foreach (var profile in directory.List())
            {
                var card = CardFromProfile(profile, now, "local-directory");
                cards.TryAdd(card.CardId, card);
            }
        }

        if (Ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false) is { } manager)
        {
            foreach (var profile in manager.List())
            {
                var card = CardFromProfile(profile, now, "persistent-agents");
                cards.TryAdd(card.CardId, card);
            }
        }

        if (Ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName, false) is { } router)
        {
            var targets = await router.ListTargetsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var target in targets)
            {
                var card = new DiscoveredAgentCard(
                    $"target:{target.TargetId}",
                    target.DisplayName,
                    $"{target.Kind} execution target ({target.TargetId})",
                    target.Kind,
                    target.Endpoint ?? target.MachineName ?? target.TargetId,
                    target.Capabilities ?? [],
                    SourceProvider: $"router:{target.Kind}",
                    DiscoveredAt: now,
                    Metadata: target.Metadata);
                cards.TryAdd(card.CardId, card);
            }
        }

        return cards.Values.ToList();
    }

    private AgentProfile? ResolveLocalProfile(AgentId agentId)
    {
        if (Ctx.Get<IAgentDirectoryStore>(IAgentDirectoryStore.ServiceName, false)?.Get(agentId) is { } stored)
            return stored;
        if (Ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false)?.Get(agentId) is { } managed)
            return managed;
        var live = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)?
            .List()
            .FirstOrDefault(agent => agent.AgentId == agentId || agent.Id.Value == agentId.Value);
        if (live is null)
            return null;
        return new AgentProfile(
            live.AgentId,
            live.AgentId.Value,
            null,
            AgentBackendKind.Local,
            live.Id,
            [live.Id],
            live.Status == AgentStatus.Running ? AgentLifecycleState.Active : AgentLifecycleState.Idle);
    }

    private static DiscoveredAgentCard CardFromProfile(AgentProfile profile, long now, string source)
        => new(
            $"local:{profile.AgentId.Value}",
            profile.Name,
            profile.Description ?? $"Agent {profile.Name}",
            profile.BackendKind,
            profile.TargetUri ?? profile.TargetMachine ?? "local",
            profile.Capabilities ?? [],
            AgentId: profile.AgentId,
            SourceProvider: source,
            DiscoveredAt: now,
            Metadata: profile.Metadata);

    private (string Name, string Endpoint) ResolveCardIdentityLocked(string cardId)
    {
        if (_knownCards.TryGetValue(cardId, out var card))
            return (card.Name, card.Endpoint);
        if (_importedCards.TryGetValue(cardId, out var imported))
            return (imported.Name, imported.Endpoint);
        if (_trustRecords.TryGetValue(cardId, out var existing))
            return (existing.Name, existing.Endpoint);
        return (cardId, "unknown");
    }

    private static bool MatchesQuery(DiscoveredAgentCard card, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var needle = query.Trim();
        return card.CardId.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || card.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || card.Description.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || card.Endpoint.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || card.Capabilities.Any(cap => cap.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private AgentTrustStoreDocument CaptureDocumentLocked()
        => new([.. _trustRecords.Values], [.. _importedCards.Values]);

    private void LoadFromDisk()
    {
        if (TrustFilePath() is not { } path)
            return;
        var doc = CollabStorage.ReadJson(path, DshCollabJsonContext.Default.AgentTrustStoreDocument);
        if (doc is null)
            return;
        lock (_gate)
        {
            foreach (var card in doc.ImportedCards)
            {
                _importedCards[card.CardId] = card;
                _knownCards[card.CardId] = card;
            }
            foreach (var record in doc.TrustRecords)
            {
                // Enforce safety invariant even if a persisted file was manually altered for non-trusted states.
                var sanitized = record.Status == AgentTrustStatus.Trusted
                    ? record
                    : record with { Permissions = NoPermissions };
                _trustRecords[record.CardId] = sanitized;
            }
        }
    }

    private void PersistTrustSync(AgentTrustStoreDocument document)
    {
        if (TrustFilePath() is { } path)
            CollabStorage.WriteJsonAtomic(path, document, DshCollabJsonContext.Default.AgentTrustStoreDocument);
    }

    private async Task PersistTrustAsync(AgentTrustStoreDocument document, CancellationToken cancellationToken)
    {
        if (TrustFilePath() is { } path)
            await CollabStorage.WriteJsonAtomicAsync(path, document, DshCollabJsonContext.Default.AgentTrustStoreDocument, cancellationToken).ConfigureAwait(false);
    }

    private string? TrustFilePath()
        => _collabDirectory is null ? null : Path.Combine(_collabDirectory, TrustFileName);

    private long NowMs() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private sealed class ProviderRegistration(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }
}
