using Cordis;
using Dsh.Boot;
using Dsh.Plugins;
using Dsh.Sdk;

[assembly: DshPlugin("@deepseek-ai/dsh-lsp")]

namespace Dsh.Lsp;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        PluginEntrypointRegistry.Register("lsp",
            static (_, _, cancellationToken) => RunAsync(cancellationToken));
        return new CallbackDisposable();
    }

    private static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var transport = new JsonRpcLineTransport(Console.In, Console.Out);
        var server = new LspServer();
        transport.RequestHandler = server.HandleRequestAsync;
        transport.Start();
        await transport.WhenClosedAsync().WaitAsync(cancellationToken);
        await transport.DisposeAsync();
        return 0;
    }

    private sealed class CallbackDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}