using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-fleet")]

namespace Dsh.Fleet;

/** Provides the plugin-lifetime owner; unloading disposes its clients while host sessions remain independent. */
public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [WorkspaceService.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var root = ctx.GetProp("dshHomePath") as string
            ?? throw new InvalidOperationException("the fleet plugin requires a harness home");
        var service = new FleetService(new HarnessHome(root), ctx);
        FleetSessionTools? tools = null;
        try
        {
            ctx.Provide(FleetService.ServiceName, service);
            ctx.Provide(IOutboundRouteController.ServiceName, service.OutboundRoute);
            tools = new FleetSessionTools(ctx, () => service.CreateClient(new FleetOptions { ClientName = "dsh-fleet-tools" }));
            var registration = ctx.Get<WorkspaceService>(WorkspaceService.ServiceName)!.Register(ctx, service);
            return new Activation(tools, registration);
        }
        catch
        {
            tools?.Dispose();
            service.Dispose();
            throw;
        }
    }

    private sealed class Activation(FleetSessionTools tools, IDisposable registration) : IDisposable
    {
        public void Dispose()
        {
            tools.Dispose();
            registration.Dispose();
        }
    }
}
