using System.Text.Json.Serialization;
using Dsh.Llm;

namespace Dsh.Core;

/** 长期 Agent 身份标识：独立于单次 SessionId，可跨越多个会话与进程重启。 */
[JsonConverter(typeof(BrandJsonConverter<AgentId>))]
public readonly record struct AgentId(string Value) : IBrand<AgentId>
{
    public static AgentId Create(string value) => new(value);

    public static AgentId FromSession(SessionId sessionId) => new(sessionId.Value);

    public static AgentId New() => new($"agent-{Guid.NewGuid():N}");

    public override string ToString() => Value;
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentLifecycleState>))]
public enum AgentLifecycleState
{
    Active,
    Idle,
    Suspended,
    Disposed,
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentBackendKind>))]
public enum AgentBackendKind
{
    Local,
    Fleet,
    Ma2a,
    A2a,
    Acp,
    Plugin,
}

/** 持久化 Agent 档案：维护长期身份、当前活跃会话及所有关联会话。 */
public sealed record AgentProfile(
    AgentId AgentId,
    string Name,
    string? Description,
    AgentBackendKind BackendKind,
    SessionId? ActiveSessionId,
    IReadOnlyList<SessionId> SessionIds,
    AgentLifecycleState State,
    string? TargetMachine = null,
    string? TargetUri = null,
    string? Cwd = null,
    AgentOptions? Options = null,
    string? AgentPreset = null,
    long CreatedAt = 0,
    long UpdatedAt = 0,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AgentProfileRegistration(
    string Name,
    string? Description = null,
    AgentBackendKind BackendKind = AgentBackendKind.Local,
    AgentId? AgentId = null,
    SessionId? InitialSessionId = null,
    string? TargetMachine = null,
    string? TargetUri = null,
    string? Cwd = null,
    AgentOptions? Options = null,
    string? AgentPreset = null,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public interface IAgentDirectoryStore
{
    const string ServiceName = "agentDirectory";

    AgentProfile? Get(AgentId id);
    IReadOnlyList<AgentProfile> List();
    void Save(AgentProfile profile);
    bool Delete(AgentId id);
}

public interface IPersistentAgentManager
{
    const string ServiceName = "persistentAgents";

    Task<AgentProfile> CreateAsync(AgentProfileRegistration registration, CancellationToken cancellationToken = default);
    AgentProfile? Get(AgentId id);
    IReadOnlyList<AgentProfile> List();
    Task<IAgent> WakeAsync(AgentId id, CancellationToken cancellationToken = default);
    Task<IAgent> WakeSessionAsync(AgentId id, SessionId sessionId, CancellationToken cancellationToken = default)
        => WakeAsync(id, cancellationToken);
    Task SuspendAsync(AgentId id, CancellationToken cancellationToken = default);
    Task<AgentProfile> AttachSessionAsync(AgentId id, SessionId sessionId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(AgentId id, CancellationToken cancellationToken = default);
}
