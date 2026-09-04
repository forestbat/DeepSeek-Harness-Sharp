using Cordis;
using Dsh.Llm;

namespace Dsh.Core;

public interface IAgent
{
    SessionId Id { get; }
    Session Session { get; }
    ScopeKey ScopeKey { get; }
    Context Ctx { get; }
    AgentStatus Status { get; }
    AgentOptions Options { get; }

    void Cancel(AgentCancelCause cause, bool keepInbox = false);
    Task WhenIdle();
    void Send(UserMessage message, string target, bool wakeup);
    void Followup(UserMessage message);
    void Steer(UserMessage message);
    void Inject(UserMessage message);
}

public enum AgentStatus
{
    Idle,
    Running,
}

public sealed record AgentOptions(
    string? Provider = null,
    string? Model = null,
    ReasoningEffortId? ReasoningEffort = null,
    int? MaxTokens = null);
