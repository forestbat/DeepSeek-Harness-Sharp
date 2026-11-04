using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-workspace")]

namespace Dsh.Workspace;

/** Owns backend registrations independently of any optional workspace integration. */
public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        var service = new WorkspaceService();
        ctx.Provide(WorkspaceService.ServiceName, service);
        return service;
    }
}
