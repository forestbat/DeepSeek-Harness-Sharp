using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Plugins;
using Dsh.Workspace;

[assembly: DshPlugin("@deepseek-ai/dsh-gui")]

namespace Dsh.Gui;

[DshEntrypoint("gui")]
public sealed class Plugin : IDshPlugin, IDshEntrypoint
{
    public string[] Inject => [IWorkspaceProvider.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var workbench = new GuiWorkbench();
        var provided = ctx.Provide(GuiWorkbench.ServiceName, workbench);
        return new PluginScope(workbench, provided);
    }

    public Task<int> RunAsync(HarnessApp app, PluginEntrypointOptions options, CancellationToken cancellationToken)
        => GuiRunner.Run(app, options.Cwd, options.ResumeSessionId, options.StartLocalHost);

    private sealed class PluginScope(GuiWorkbench workbench, EffectHandle provided) : IDisposable
    {
        public void Dispose()
        {
            provided.Dispose();
            workbench.Dispose();
        }
    }
}
