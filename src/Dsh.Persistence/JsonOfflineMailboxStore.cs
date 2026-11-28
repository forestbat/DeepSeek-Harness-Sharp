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
            var path = PathFor(envelope.TargetAgentId);
            using var fileLock = OwnerOnlyAtomicFile.AcquireLock(path);
            var queue = ReloadLocked(envelope.TargetAgentId, path);
            if (IsDuplicate(queue, envelope))
                return false;
            queue.Add(envelope);
            PersistLocked(path, queue);
            return true;
        }
    }

    public IReadOnlyList<AgentEnvelope> Peek(AgentId agentId)
    {
        lock (_gate)
        {
            var path = PathFor(agentId);
            using var fileLock = OwnerOnlyAtomicFile.AcquireLock(path);
            return [.. ReloadLocked(agentId, path)];
        }
    }

    public IReadOnlyList<AgentEnvelope> Drain(AgentId agentId)
    {
        lock (_gate)
        {
            var path = PathFor(agentId);
            using var fileLock = OwnerOnlyAtomicFile.AcquireLock(path);
            var queue = ReloadLocked(agentId, path);
            if (queue.Count == 0)
                return [];
            List<AgentEnvelope> drained = [.. queue];
            queue.Clear();
            PersistLocked(path, queue);
            return drained;
        }
    }

    public bool Remove(AgentId agentId, string envelopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeId);
        lock (_gate)
        {
            var path = PathFor(agentId);
            using var fileLock = OwnerOnlyAtomicFile.AcquireLock(path);
            var queue = ReloadLocked(agentId, path);
            var removed = queue.RemoveAll(item => string.Equals(item.EnvelopeId, envelopeId, StringComparison.Ordinal));
            if (removed == 0)
                return false;
            PersistLocked(path, queue);
            return true;
        }
    }

    private List<AgentEnvelope> ReloadLocked(AgentId agentId, string path)
    {
        var loaded = OwnerOnlyAtomicFile.ReadJson(path, DshPersistenceJsonContext.Default.ListAgentEnvelope) ?? [];
        _mailboxes[agentId] = loaded;
        return loaded;
    }

    private static void PersistLocked(string path, List<AgentEnvelope> queue)
    {
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
