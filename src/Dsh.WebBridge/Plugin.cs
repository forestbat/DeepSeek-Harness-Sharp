using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-web-bridge")]

namespace Dsh.WebBridge;

/** Activated only by the independent web executable. The activation owns actual Fleet links and listener lifetime, so disabling or unloading it stops the bridge. */
[DshEntrypoint("web")]
public sealed class Plugin : IDshPlugin, IDshEntrypoint
{
    public string[] Inject => [];
    public IDisposable Apply(Context ctx, object? config)
    {
        var runtime = new WebBridgeRuntime(OutboundProxy.FromContext(ctx), FleetService.DefaultSshTransportAdapter);
        ctx.Provide(WebBridgeRuntime.ServiceName, runtime);
        return runtime;
    }
    public Task<int> RunAsync(HarnessApp app, PluginEntrypointOptions options, CancellationToken cancellationToken)
    {
        var runtime = app.Ctx.Get<WebBridgeRuntime>(WebBridgeRuntime.ServiceName)
            ?? throw new InvalidOperationException("the web bridge plugin is not active");
        var application = app.Ctx.Get<IWebBridgeApplication>(WebBridgeRuntime.ApplicationServiceName)
            ?? throw new InvalidOperationException("the web entrypoint requires the independent dsharp-web host");
        return application.RunAsync(runtime, options, cancellationToken);
    }
}
