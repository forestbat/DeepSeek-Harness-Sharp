using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Persistence;

/** Persists queued offline envelopes per agent under `<home>/agents/mailbox/<agentId>.json` with idempotency deduplication. */
public sealed class JsonOfflineMailboxStore : Service, IOfflineMailboxStore
{
    private const string MailboxDirectoryName = "mailbox";

    private readonly Lock _gate = new();
    private readonly string _mailboxDir;
    private readonly Dictionary<AgentId, List<AgentEnvelope>> _mailboxes = [];

    public JsonOfflineMailboxStore(Context ctx, string? agentsRootPath = null)
        : base(ctx, IOfflineMailboxStore.ServiceName)
    {
        var agentsRoot = agentsRootPath
            ?? DshPaths.FromContext(ctx)?.Path("agents")
            ?? throw new InvalidOperationException("dshPaths/dshHomePath is required for JsonOfflineMailboxStore");
        _mailboxDir = Path.Combine(agentsRoot, MailboxDirectoryName);
    }

    public bool Enqueue(AgentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            var queue = GetOrLoadLocked(envelope.TargetAgentId);
            if (IsDuplicate(queue, envelope))
                return false;
            queue.Add(envelope);
            PersistLocked(envelope.TargetAgentId, queue);
            return true;
        }
    }

    public IReadOnlyList<AgentEnvelope> Peek(AgentId agentId)
    {
        lock (_gate)
            return [.. GetOrLoadLocked(agentId)];
    }

    public IReadOnlyList<AgentEnvelope> Drain(AgentId agentId)
    {
        lock (_gate)
        {
            var queue = GetOrLoadLocked(agentId);
            if (queue.Count == 0)
                return [];
            List<AgentEnvelope> drained = [.. queue];
            queue.Clear();
            PersistLocked(agentId, queue);
            return drained;
        }
    }

    public bool Remove(AgentId agentId, string envelopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeId);
        lock (_gate)
        {
            var queue = GetOrLoadLocked(agentId);
            var removed = queue.RemoveAll(item => string.Equals(item.EnvelopeId, envelopeId, StringComparison.Ordinal));
            if (removed == 0)
                return false;
            PersistLocked(agentId, queue);
            return true;
        }
    }

    private List<AgentEnvelope> GetOrLoadLocked(AgentId agentId)
    {
        if (_mailboxes.TryGetValue(agentId, out var existing))
            return existing;
        var loaded = OwnerOnlyAtomicFile.ReadJson(PathFor(agentId), DshPersistenceJsonContext.Default.ListAgentEnvelope) ?? [];
        _mailboxes[agentId] = loaded;
        return loaded;
    }

    private void PersistLocked(AgentId agentId, List<AgentEnvelope> queue)
    {
        var path = PathFor(agentId);
        if (queue.Count == 0)
        {
            OwnerOnlyAtomicFile.TryDelete(path);
            return;
        }
        OwnerOnlyAtomicFile.WriteJson(path, queue, DshPersistenceJsonContext.Default.ListAgentEnvelope);
    }

    private string PathFor(AgentId agentId)
        => Path.Combine(_mailboxDir, $"{JsonlLayout.EncodeSegment(agentId.Value)}.json");

    private static bool IsDuplicate(List<AgentEnvelope> queue, AgentEnvelope incoming)
    {
        foreach (var existing in queue)
        {
            if (string.Equals(existing.EnvelopeId, incoming.EnvelopeId, StringComparison.Ordinal))
                return true;
            if (incoming.IdempotencyKey is { Length: > 0 } key
                && string.Equals(existing.IdempotencyKey, key, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
