using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Plugins;

[assembly: DshPlugin("@deepseek-ai/dsh-tui")]

namespace Dsh.Tui;

[DshEntrypoint("tui")]
public sealed class Plugin : IDshPlugin, IDshEntrypoint
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config) => new CallbackDisposable();

    public Task<int> RunAsync(HarnessApp app, PluginEntrypointOptions options, CancellationToken cancellationToken)
        => TuiRunner.Run(app, options.Cwd, options.Gpu, options.Shell, options.GpuScreenshotPath, options.GpuCard);

    private sealed class CallbackDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
