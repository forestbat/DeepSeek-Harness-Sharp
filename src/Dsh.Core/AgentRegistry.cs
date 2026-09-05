using Cordis;
using Dsh.Llm;

namespace Dsh.Core;

public sealed record CreateAgentOptions(
    SessionId? SessionId = null,
    string? Cwd = null,
    AgentOptions? AgentOptions = null,
    string? AgentPreset = null,
    IReadOnlyList<SessionEvent>? Seed = null,
    long? InheritedEventCount = null,
    Action<Context>? Setup = null);

public sealed record ResumeAgentOptions(
    SessionId SessionId,
    AgentOptions? AgentOptions = null,
    Action<Context>? Setup = null);

public sealed record AgentHandle(IAgent Agent, IDisposable Dispose);

public interface IAgentFactory
{
    Task<IAgent> CreateAgent(Context owner, CreateAgentOptions options, CancellationToken signal = default);

    Task<IAgent> Resume(Context owner, ResumeAgentOptions options, CancellationToken signal = default);
}

public sealed class AgentRegistry : Service
{
    public const string ServiceName = "agents";

    private static readonly AsyncLocal<IAgent?> InitiatorSlot = new();

    private readonly Dictionary<SessionId, IAgent> _agents = [];
    private readonly Dictionary<SessionId, IAgent> _owners = [];
    private IAgentFactory? _factory;

    public AgentRegistry(Context ctx) : base(ctx, ServiceName)
    {
    }

    public IAgent? CurrentInitiator() => InitiatorSlot.Value;

    public IAgent RequireInitiator()
        => InitiatorSlot.Value ?? throw new InvalidOperationException("no initiator agent on this async context");

    public T WithInitiator<T>(IAgent agent, Func<T> operation)
    {
        var prior = InitiatorSlot.Value;
        InitiatorSlot.Value = agent;
        try
        {
            return operation();
        }
        finally
        {
            InitiatorSlot.Value = prior;
        }
    }

    public async Task<T> WithInitiatorAsync<T>(IAgent agent, Func<Task<T>> operation)
    {
        var prior = InitiatorSlot.Value;
        InitiatorSlot.Value = agent;
        try
        {
            return await operation();
        }
        finally
        {
            InitiatorSlot.Value = prior;
        }
    }

    public IDisposable SetFactory(IAgentFactory factory)
    {
        if (_factory is not null)
            throw new InvalidOperationException("an agent factory is already registered");
        _factory = factory;
        return new FactoryRegistration(() => _factory = null);
    }

    public async Task<AgentHandle> Create(CreateAgentOptions options, CancellationToken signal = default)
    {
        var factory = _factory ?? throw new InvalidOperationException("no agent factory registered");
        var agent = await factory.CreateAgent(Ctx, options, signal);
        Register(agent);
        return new AgentHandle(agent, new AgentDetach(() => Unregister(agent.Id)));
    }

    public async Task<AgentHandle> Resume(ResumeAgentOptions options, CancellationToken signal = default)
    {
        var factory = _factory ?? throw new InvalidOperationException("no agent factory registered");
        var agent = await factory.Resume(Ctx, options, signal);
        Register(agent);
        return new AgentHandle(agent, new AgentDetach(() => Unregister(agent.Id)));
    }

    public void Register(IAgent agent) => Enter(agent, null);

    public void Enter(IAgent agent, IAgent? owner)
    {
        if (_agents.ContainsKey(agent.Id))
            throw new InvalidOperationException($"agent \"{agent.Id}\" is already registered");
        _agents[agent.Id] = agent;
        if (owner is not null)
            _owners[agent.Id] = owner;
        Ctx.Emit(AgentEventNames.Created, new { Agent = agent });
    }

    public IAgent? Get(SessionId id) => _agents.TryGetValue(id, out var agent) ? agent : null;

    public IReadOnlyList<IAgent> List() => _agents.Values.ToList();

    public bool IsOwnedBy(SessionId id, IAgent owner) => _owners.TryGetValue(id, out var existing) && ReferenceEquals(existing, owner);

    public IReadOnlyList<IAgent> Roots() => _agents.Values.Where(agent => !_owners.ContainsKey(agent.Id)).ToList();

    private void Unregister(SessionId id)
    {
        if (_agents.Remove(id, out var agent))
        {
            _owners.Remove(id);
            Ctx.Emit(AgentEventNames.Disposed, new { Agent = agent });
        }
    }

    private sealed class FactoryRegistration(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }

    private sealed class AgentDetach(Action detach) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            detach();
        }
    }
}
