using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-fleet")]

namespace Dsh.Fleet;

/** Provides the plugin-lifetime owner; unloading disposes its clients while host sessions remain independent. */
public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        var root = ctx.GetProp("dshHomePath") as string
            ?? throw new InvalidOperationException("the fleet plugin requires a harness home");
        var service = new FleetService(new HarnessHome(root), ctx);
        ctx.Provide(FleetService.ServiceName, service);
        ctx.Provide(IOutboundRouteController.ServiceName, service.OutboundRoute);
        return service;
    }
}
