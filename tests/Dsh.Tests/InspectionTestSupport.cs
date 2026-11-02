using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Tests;

internal static class InspectionTestPlugin
{
    public static PluginDefinition Definition(string package)
    {
        var plugin = new Dsh.Inspection.Plugin(package);
        return new PluginDefinition
        {
            Name = package,
            Inject = plugin.Inject,
            Apply = (ctx, config, _) => Task.FromResult<object?>(plugin.Apply(ctx, config)),
        };
    }
}

internal sealed class InspectionTestAgent : IAgent
{
    private InspectionTestAgent(Context ctx)
    {
        Ctx = ctx;
        var id = SessionId.Create($"session-{Guid.NewGuid():N}");
        Session = Session.Create(id, null, new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 0,
            Cwd = Path.GetTempPath(),
            IsSeeded = false,
        });
    }

    public static InspectionTestAgent Open(Context ctx)
    {
        var agent = new InspectionTestAgent(ctx);
        agent.Session.Append(new TurnStartPayload(1));
        return agent;
    }

    public SessionId Id => Session.Id;
    public Session Session { get; }
    public ScopeKey ScopeKey { get; } = new();
    public Context Ctx { get; }
    public AgentStatus Status => AgentStatus.Idle;
    public AgentOptions Options { get; } = new();

    public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
    public Task WhenIdle() => Task.CompletedTask;
    public void Send(UserMessage message, string target, bool wakeup) { }
    public void Followup(UserMessage message) { }
    public void Steer(UserMessage message) { }
    public void Inject(UserMessage message) { }
}

internal sealed class RecordingPluginManager : IPluginManager
{
    public List<string> Calls { get; } = [];

    public IReadOnlyList<string> PackageNames => ["sample-plugin"];

    public IReadOnlyList<PluginSkip> LoadFailures => [];

    public string Describe(string package)
    {
        Calls.Add($"describe:{package}");
        return "active [compiled-in]";
    }

    public Task<string> AddAsync(string packageOrPath, ToolRunContext? run = null)
    {
        Calls.Add($"add:{packageOrPath}");
        return Task.FromResult($"plugin {packageOrPath} activated");
    }

    public Task<string> RemoveAsync(string package, bool force = false, ToolRunContext? run = null)
    {
        Calls.Add($"remove:{package}:{force}");
        return Task.FromResult($"plugin {package} removed");
    }

    public Task<string> DisableAsync(string package, ToolRunContext? run = null)
    {
        Calls.Add($"disable:{package}");
        return Task.FromResult($"plugin {package} disabled");
    }

    public Task<string> EnableAsync(string package, ToolRunContext? run = null)
    {
        Calls.Add($"enable:{package}");
        return Task.FromResult($"plugin {package} enabled");
    }

    public Task<string> SetPermissionAsync(string package, PluginPermission permission, bool allowed, CancellationToken cancellationToken = default, ToolRunContext? run = null)
    {
        Calls.Add($"permission:{package}:{permission}:{allowed}");
        return Task.FromResult($"plugin {package} {permission} {allowed}");
    }
}
