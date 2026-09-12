using Cordis;
using Dsh.Boot;
using Dsh.Plugins;

[assembly: DshPlugin("@deepseek-ai/dsh-gui")]

namespace Dsh.Gui;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        PluginEntrypointRegistry.Register("gui",
            static (app, options, _) => GuiRunner.Run(app, options.Cwd));
        return new CallbackDisposable();
    }

    private sealed class CallbackDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
