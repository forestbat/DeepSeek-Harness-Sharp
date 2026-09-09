using System.Text.Json;
using Cordis;
using Dsh.Boot;
using Dsh.Plugins;
using Dsh.Sdk;

[assembly: DshPlugin("@deepseek-ai/dsh-acp")]

namespace Dsh.Acp;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        PluginEntrypointRegistry.Register("acp",
            static (app, _, cancellationToken) => RunAsync(app, cancellationToken));
        return new CallbackDisposable();
    }

    private static async Task<int> RunAsync(HarnessApp app, CancellationToken cancellationToken)
    {
        var transport = new JsonRpcLineTransport(Console.In, Console.Out);
        var server = new AcpServer(app.Ctx, transport, app.Provider, app.Model);
        transport.RequestHandler = server.HandleRequestAsync;
        transport.NotificationHandler = (method, parameters) =>
        {
            if (method == "session/cancel")
                server.Cancel(parameters);
        };
        transport.Start();
        await transport.WhenClosedAsync().WaitAsync(cancellationToken);
        await server.CloseAllAsync();
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