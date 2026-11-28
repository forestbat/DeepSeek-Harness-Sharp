using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Persistence;

/** Persists long-lived agent profiles under `<home>/agents/profiles.json` using atomic owner-only writes. */
public sealed class JsonAgentProfileStore : Service, IAgentDirectoryStore
{
    private const string ProfilesFileName = "profiles.json";

    private readonly Lock _gate = new();
    private readonly string _profilesPath;
    private readonly Dictionary<AgentId, AgentProfile> _profiles = [];

    public JsonAgentProfileStore(Context ctx, string? agentsRootPath = null)
        : base(ctx, IAgentDirectoryStore.ServiceName)
    {
        var agentsRoot = agentsRootPath
            ?? DshPaths.FromContext(ctx)?.Path("agents")
            ?? throw new InvalidOperationException("dshPaths/dshHomePath is required for JsonAgentProfileStore");
        _profilesPath = Path.Combine(agentsRoot, ProfilesFileName);
        Load();
    }

    public AgentProfile? Get(AgentId id)
    {
        lock (_gate)
            return _profiles.GetValueOrDefault(id);
    }

    public IReadOnlyList<AgentProfile> List()
    {
        lock (_gate)
        {
            return
            [
                .. _profiles.Values
                    .OrderBy(profile => profile.CreatedAt)
                    .ThenBy(profile => profile.AgentId.Value, StringComparer.Ordinal),
            ];
        }
    }

    public void Save(AgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            _profiles[profile.AgentId] = profile;
            PersistLocked();
        }
    }

    public bool Delete(AgentId id)
    {
        lock (_gate)
        {
            if (!_profiles.Remove(id))
                return false;
            PersistLocked();
            return true;
        }
    }

    private void Load()
    {
        var loaded = OwnerOnlyAtomicFile.ReadJson(_profilesPath, DshPersistenceJsonContext.Default.ListAgentProfile);
        if (loaded is null)
            return;
        lock (_gate)
        {
            _profiles.Clear();
            foreach (var profile in loaded)
                _profiles[profile.AgentId] = profile;
        }
    }

    private void PersistLocked()
    {
        List<AgentProfile> snapshot =
        [
            .. _profiles.Values
                .OrderBy(profile => profile.CreatedAt)
                .ThenBy(profile => profile.AgentId.Value, StringComparer.Ordinal),
        ];
        OwnerOnlyAtomicFile.WriteJson(_profilesPath, snapshot, DshPersistenceJsonContext.Default.ListAgentProfile);
    }
}
