using Cordis;
using Dsh.Boot;
using Dsh.Plugins;

[assembly: DshPlugin("@deepseek-ai/dsh-tui")]

namespace Dsh.Tui;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        PluginEntrypointRegistry.Register("tui",
            static (app, options, _) => TuiRunner.Run(app, options.Cwd));
        return new CallbackDisposable();
    }

    private sealed class CallbackDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}