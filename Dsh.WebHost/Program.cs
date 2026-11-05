using Dsh.Boot;
using Dsh.Host;
using Dsh.Plugins;
using Dsh.WebBridge;
using Dsh.ScriptHost;

global::Dsh.Plugins.Generated.DshPluginCatalog.RegisterAll();
if (PluginExecutionRuntime.TryRunWorkerAsync(args) is { } worker)
    return await worker;
if (args.Contains(PtcScriptHost.HostArgument))
    return await PtcScriptHost.RunAsync(args);

// Detached Host and PTY daemon children reuse this executable and the shared command handler.
if (args.Contains("host") || args is ["tui", "daemon"])
    return await HarnessEntrypoint.RunAsync(args);
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("usage: dsharp-web [--port N] [--print-token]");
    return 0;
}
var home = HarnessStorage.ResolveDefaultHome();
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
using var termination = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
using var app = await ConfigBoot.Compose(new HarnessOptions(home, Directory.GetCurrentDirectory(), IsTui: false));
app.Ctx.Provide(WebBridgeRuntime.ApplicationServiceName, new WebBridgeApplication());
Task StartHost(string root, CancellationToken cancellationToken)
    => HostAutostart.EnsureRunningAsync(HarnessHome.Resolve(root), token: null, cancellationToken);
try
{
    return await app.RunEntrypointAsync("web", new PluginEntrypointOptions(home, Directory.GetCurrentDirectory(), StartLocalHost: StartHost, Arguments: args), stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
