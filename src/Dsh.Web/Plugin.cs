using Cordis;
using Dsh.Boot;
using Dsh.Plugins;

[assembly: DshPlugin("@deepseek-ai/dsh-web")]

namespace Dsh.Web;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        PluginEntrypointRegistry.Register("web",
            static (_, _, cancellationToken) => RunAsync(cancellationToken));
        return new CallbackDisposable();
    }

    private static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var server = new WebProfileServer();
        Console.WriteLine($"dsh web: http://127.0.0.1:{server.Port}");
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        finally
        {
            if (server is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }

    private sealed class CallbackDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}