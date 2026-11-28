using Dsh.Boot;
using Dsh.Core;
using Dsh.Agent;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Transport;

[assembly: DshPlugin("@deepseek-ai/dsh-fleet")]

namespace Dsh.Fleet;

/** Provides the plugin-lifetime owner; unloading disposes its clients while host sessions remain independent. */
public sealed class Plugin : IDshPlugin
{
    /**
 * 插件按服务名声明依赖: 这里要 ToolRuntime, 因为 FleetSessionTools 在 Apply 里就把它注册到工具运行时。
 * 只声明 WorkspaceService 会让激活顺序靠运气 —— 工具运行时还没就位时 Apply 会失败, 整个 fleet 后端随之缺席。
 */
public string[] Inject => [WorkspaceService.ServiceName, ToolRuntime.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var root = ctx.GetProp("dshHomePath") as string
            ?? throw new InvalidOperationException("the fleet plugin requires a harness home");
        var service = new FleetService(new HarnessHome(root), ctx);
        FleetSessionTools? tools = null;
        FleetAgentExecutionBackend? backend = null;
        IDisposable? backendRegistration = null;
        try
        {
            ctx.Provide(FleetService.ServiceName, service);
            ctx.Provide(IOutboundRouteController.ServiceName, service.OutboundRoute);
            tools = new FleetSessionTools(ctx, () => service.CreateClient(new FleetOptions { ClientName = "dsh-fleet-tools" }));
            var registration = ctx.Get<WorkspaceService>(WorkspaceService.ServiceName)!.Register(ctx, service);
            if (ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName, false) is { } router)
            {
                backend = new FleetAgentExecutionBackend(service);
                backendRegistration = router.RegisterBackend(backend);
            }
            return new Activation(tools, registration, backendRegistration, backend);
        }
        catch
        {
            backendRegistration?.Dispose();
            backend?.Dispose();
            tools?.Dispose();
            service.Dispose();
            throw;
        }
    }

    private sealed class Activation(
        FleetSessionTools tools,
        IDisposable registration,
        IDisposable? backendRegistration,
        FleetAgentExecutionBackend? backend) : IDisposable
    {
        public void Dispose()
        {
            backendRegistration?.Dispose();
            backend?.Dispose();
            tools.Dispose();
            registration.Dispose();
        }
    }
}
