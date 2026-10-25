using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-fleet-iroh")]

namespace Dsh.Fleet.Iroh;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [FleetService.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        FleetService.DefaultSshTransportAdapter = new IrohSshTransport();
        FleetService.IrohSetup = new IrohSetupService();
        return new CallbackDisposable(() =>
        {
            FleetService.DefaultSshTransportAdapter = null;
            FleetService.IrohSetup = null;
        });
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
