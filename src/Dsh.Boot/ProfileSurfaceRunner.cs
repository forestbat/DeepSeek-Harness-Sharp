using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Cordis;

namespace Dsh.Boot;

public sealed record ProfileSurfaceRunOptions(
    HarnessHome Home,
    string Cwd,
    string? Config,
    IReadOnlyList<Dictionary<string, object?>>? Patches,
    string? SettingsConfig);

public sealed record ProfileSurfaceDescriptor(
    string Name,
    Func<ProfileSurfaceRunOptions, CancellationToken, Task<int>> Run);

public static class ProfileSurfaceRegistry
{
    public static readonly IReadOnlyDictionary<string, ProfileSurfaceDescriptor> All = new Dictionary<string, ProfileSurfaceDescriptor>(StringComparer.Ordinal)
    {
        ["tui"] = new("tui", RunTuiAsync),
        ["gui"] = new("gui", RunGuiAsync),
        ["web"] = new("web", RunWebAsync),
        ["acp"] = new("acp", RunAcpAsync),
        ["lsp"] = new("lsp", RunLspAsync),
    };

    public static async Task<int> RunAsync(string name, ProfileSurfaceRunOptions options, CancellationToken cancellationToken = default)
    {
        if (!All.TryGetValue(name, out var descriptor))
            throw new CordisException("UNKNOWN_SURFACE", $"unknown profile surface \"{name}\"");
        return await descriptor.Run(options, cancellationToken);
    }

    private static async Task<int> RunTuiAsync(ProfileSurfaceRunOptions options, CancellationToken cancellationToken)
    {
        var method = RequireMethod("Dsh.Tui.TuiRunner", "Dsh.Tui", "Run");
        var task = (Task<int>)method.Invoke(null, [options.Home, options.Cwd, options.Config, options.Patches, options.SettingsConfig])!;
        return await task.WaitAsync(cancellationToken);
    }

    private static async Task<int> RunGuiAsync(ProfileSurfaceRunOptions options, CancellationToken cancellationToken)
    {
        var method = RequireMethod("Dsh.Gui.GuiRunner", "Dsh.Gui", "Run");
        var task = (Task<int>)method.Invoke(null, [options.Home, options.Cwd, options.Config, options.Patches, options.SettingsConfig])!;
        return await task.WaitAsync(cancellationToken);
    }

    private static async Task<int> RunWebAsync(ProfileSurfaceRunOptions options, CancellationToken cancellationToken)
    {
        var type = RequireType("Dsh.Web.WebProfileServer", "Dsh.Web");
        var server = Activator.CreateInstance(type)!;
        var port = (int)type.GetProperty("Port")!.GetValue(server)!;
        Console.WriteLine($"dsh web: http://127.0.0.1:{port}");
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

    private static async Task<int> RunAcpAsync(ProfileSurfaceRunOptions options, CancellationToken cancellationToken)
    {
        using var app = await ComposeAppAsync(options);
        var transport = CreateTransport();
        var serverType = RequireType("Dsh.Acp.AcpServer", "Dsh.Acp");
        var server = Activator.CreateInstance(serverType, app.Ctx, transport, app.Provider, app.Model)!;

        var handlerType = transport.GetType().Assembly.GetType("Dsh.Sdk.JsonRpcRequestHandler")!;
        transport.GetType().GetProperty("RequestHandler")!.SetValue(transport,
            Delegate.CreateDelegate(handlerType, server, serverType.GetMethod("HandleRequestAsync")!));

        var notificationHandlerType = transport.GetType().Assembly.GetType("Dsh.Sdk.JsonRpcNotificationHandler")!;
        var bridge = new AcpNotificationBridge(server, serverType.GetMethod("Cancel")!);
        transport.GetType().GetProperty("NotificationHandler")!.SetValue(transport,
            Delegate.CreateDelegate(notificationHandlerType, bridge, bridge.GetType().GetMethod(nameof(AcpNotificationBridge.Handle))!));

        transport.GetType().GetMethod("Start")!.Invoke(transport, null);
        await (Task)transport.GetType().GetMethod("WhenClosedAsync")!.Invoke(transport, null)!;
        await (Task)serverType.GetMethod("CloseAllAsync")!.Invoke(server, null)!;
        await DisposeTransportAsync(transport);
        return 0;
    }

    private static async Task<int> RunLspAsync(ProfileSurfaceRunOptions options, CancellationToken cancellationToken)
    {
        var transport = CreateTransport();
        var serverType = RequireType("Dsh.Lsp.LspServer", "Dsh.Lsp");
        var server = Activator.CreateInstance(serverType)!;
        var handlerType = transport.GetType().Assembly.GetType("Dsh.Sdk.JsonRpcRequestHandler")!;
        transport.GetType().GetProperty("RequestHandler")!.SetValue(transport,
            Delegate.CreateDelegate(handlerType, server, serverType.GetMethod("HandleRequestAsync")!));
        transport.GetType().GetMethod("Start")!.Invoke(transport, null);
        await (Task)transport.GetType().GetMethod("WhenClosedAsync")!.Invoke(transport, null)!;
        await DisposeTransportAsync(transport);
        return 0;
    }

    private static async Task<HarnessApp> ComposeAppAsync(ProfileSurfaceRunOptions options)
    {
        var harnessOptions = new HarnessOptions(options.Home, options.Cwd, SettingsConfig: options.SettingsConfig);
        if (options.Config is not null)
            return await ConfigBoot.Compose(options.Config, harnessOptions, patches: options.Patches);
        if (options.Patches is { Count: > 0 })
            return await ConfigBoot.ComposeProfile("sdk", options.Patches, harnessOptions);
        return HarnessComposer.Compose(harnessOptions);
    }

    private static object CreateTransport()
        => Activator.CreateInstance(RequireType("Dsh.Sdk.JsonRpcLineTransport", "Dsh.Sdk"), Console.In, Console.Out)!;

    private static async Task DisposeTransportAsync(object transport)
    {
        if (transport is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }

    private static MethodInfo RequireMethod(string typeName, string assemblyName, string methodName)
    {
        var type = RequireType(typeName, assemblyName);
        return type.GetMethod(methodName)
            ?? throw new CordisException("SURFACE_METHOD_NOT_FOUND", $"surface method {methodName} not found on {typeName}");
    }

    private static Type RequireType(string typeName, string assemblyName)
    {
        var type = Type.GetType($"{typeName}, {assemblyName}", throwOnError: false);
        if (type is null)
        {
            var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (File.Exists(path))
            {
                try
                {
                    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                    type = assembly.GetType(typeName);
                }
                catch
                {
                    // Fall through to the already-loaded assembly scan below.
                }
            }
        }
        type ??= AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(typeName)).FirstOrDefault(candidate => candidate is not null);
        if (type is null)
            throw new CordisException("SURFACE_NOT_FOUND", $"surface assembly \"{assemblyName}\" is not available: {typeName}");
        return type;
    }

    private sealed class AcpNotificationBridge(object server, MethodInfo cancelMethod)
    {
        public void Handle(string method, JsonElement? parameters)
        {
            if (method == "session/cancel")
                cancelMethod.Invoke(server, [parameters]);
        }
    }
}