using System.Reflection;
using System.Runtime.CompilerServices;
using Dsh.Core;
using Dsh.Ide.Bridge;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Plugins;

[assembly: DshPlugin("test/local")]

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class PluginHostTests
{
    [Fact]
    public async Task RegisterCompiledIn_RegistersDefinitionAndActivates()
    {
        TestPlugin.Applied = false;
        var host = new PluginHost();
        host.RegisterCompiledIn();

        Assert.True(host.Catalog.TryDescribe("test/local", out var descriptor));
        Assert.Equal(PluginForm.CompiledIn, descriptor.Form);
        Assert.True(host.Catalog.TryCreateDefinition("test/local", out var definition));

        var web = Assert.Single(host.Catalog.Descriptors, entry => entry.Entry == "web");
        Assert.Equal("@deepseek-ai/dsh-web-bridge", web.Package);
        Assert.True(host.Catalog.TryGet(web.Package, out var createWeb));
        Assert.IsType<Dsh.WebBridge.Plugin>(createWeb());
        Assert.True(host.Catalog.TryDescribe("@deepseek-ai/dsh-fleet", out _));
        Assert.Contains(host.Catalog.Descriptors, entry => entry.Package == "@deepseek-ai/dsh-tool-web");

        var ctx = new Context();
        var activation = ctx.Plugin(definition!);
        await activation.WaitAsync();
        Assert.True(TestPlugin.Applied);
        Assert.Equal(ActivationState.Active, activation.State);
    }

    [Fact(Timeout = 5000)]
    public async Task DisabledWebBridgePlugin_CannotResolveOrRunWebEntry()
    {
        var root = TempTree.CreateSocketDirectory("wpo");
        try
        {
            var home = Dsh.Boot.HarnessHome.Resolve(root);
            var settings = Dsh.Boot.HarnessSettings.Load(home);
            settings.Plugins["@deepseek-ai/dsh-web-bridge"] = new Dsh.Boot.PluginSetting { Enabled = false };
            settings.SavePlugins(home);
            using var app = await Dsh.Boot.ConfigBoot.Compose(new Dsh.Boot.HarnessOptions(home, Cwd: root));
            Assert.Null(app.Composition!.Find("@deepseek-ai/dsh-web-bridge"));
            Assert.Null(app.Ctx.Get(Dsh.WebBridge.WebBridgeRuntime.ServiceName, false));
            var failure = await Assert.ThrowsAsync<RuntimeException>(() => app.RunEntrypointAsync(
                "web", new Dsh.Boot.PluginEntrypointOptions(home, root,
                    Arguments: [Dsh.WebBridge.WebBridgeCommand.PortOption, "0"]), TestContext.Current.CancellationToken));
            Assert.Equal("ENTRYPOINT_NOT_ACTIVE", failure.Code);
            Assert.False(File.Exists(Dsh.WebBridge.WebBridgeEndpoint.PortFile(root)));
            Assert.False(File.Exists(Dsh.WebBridge.WebBridgeEndpoint.TokenFile(root)));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory(Timeout = 5000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceBackend_DeactivationClosesOwnedLinksWithoutIdeButKeepsHostSessions(bool unloadWorkspace)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await HarnessHostFixture.CreateAsync("fpl", ct);
        await server.StopHostAsync();
        await server.Environment.App.Composition!.Find("@deepseek-ai/dsh-ide")!.DeactivateAsync();
        await server.StartHostAsync(ct);
        var host = new PluginHost();
        host.RegisterCompiledIn();
        Assert.True(host.Catalog.TryCreateDefinition("@deepseek-ai/dsh-workspace", out var workspaceDefinition));
        Assert.True(host.Catalog.TryCreateDefinition("@deepseek-ai/dsh-fleet", out var definition));
        var ctx = new Context();
        ctx.SetOwn("dshHomePath", server.Root);
        var workspaceActivation = ctx.Plugin(workspaceDefinition!);
        await workspaceActivation.WaitAsync();
        var activation = ctx.Plugin(definition!);
        await activation.WaitAsync();
        Assert.Equal(ActivationState.Active, activation.State);
        Assert.Null(ctx.Get<IEditorService>(IEditorService.ServiceName, false));
        var registry = Assert.IsType<WorkspaceService>(ctx.Get(WorkspaceService.ServiceName));
        var provider = Assert.IsType<Dsh.Fleet.FleetService>(ctx.Get<IWorkspaceProvider>(IWorkspaceProvider.ServiceName));
        var services = provider.Create(new Dsh.Boot.HarnessHome(server.Root), "workspace-plugin-test");
        var client = services.CreateClient();
        client.Start();
        var lease = await Assert.Single(client.Machines).WaitConnectedAsync(ct);
        Assert.Null(lease.Client.GetCapability<IRemoteEditors>());
        var session = await lease.Client.CreateSessionAsync(server.Root, ct);
        try
        {
            await (unloadWorkspace ? workspaceActivation : activation).DeactivateAsync();
            Assert.True(lease.Lifetime.IsCancellationRequested);
            Assert.Null(ctx.Get(Dsh.Fleet.FleetService.ServiceName, false));
            Assert.Null(ctx.Get<IWorkspaceProvider>(IWorkspaceProvider.ServiceName, false));
            Assert.Throws<ObjectDisposedException>(() => services.CreateClient());
            await client.DisposeAsync();
            provider.Dispose();
            if (unloadWorkspace)
                Assert.Throws<ObjectDisposedException>(() => registry.Register(ctx, provider));
            var observer = await server.ConnectAsync("after-fleet-unload", ct);
            Assert.Contains(await observer.ListHostSessionsAsync(ct), item => item.SessionId == session.Id);
            if (!unloadWorkspace)
            {
                activation = ctx.Plugin(definition!);
                await activation.WaitAsync();
                var replacement = Assert.IsType<Dsh.Fleet.FleetService>(ctx.Get<IWorkspaceProvider>(IWorkspaceProvider.ServiceName));
                Assert.NotSame(provider, replacement);
                await using var restored = replacement.Create(new Dsh.Boot.HarnessHome(server.Root), "workspace-plugin-reload").CreateClient();
                restored.Start();
                var restoredLease = await Assert.Single(restored.Machines).WaitConnectedAsync(ct);
                Assert.Contains(await restoredLease.Client.ListHostSessionsAsync(ct), item => item.SessionId == session.Id);
            }
        }
        finally
        {
            await activation.DeactivateAsync();
            await workspaceActivation.DeactivateAsync();
        }
    }

    [Fact(Timeout = 5000)]
    public async Task WebBridgePlugin_DeactivationStopsServerAndRemovesCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("wpl");
        var app = await Dsh.Boot.ConfigBoot.Compose(new Dsh.Boot.HarnessOptions(new Dsh.Boot.HarnessHome(root), Cwd: root));
        var ctx = app.Ctx;
        var activation = Assert.IsType<PluginActivation>(app.Composition!.Find("@deepseek-ai/dsh-web-bridge"));
        Assert.Equal(ActivationState.Active, activation.State);
        var runtime = Assert.IsType<Dsh.WebBridge.WebBridgeRuntime>(ctx.Get(Dsh.WebBridge.WebBridgeRuntime.ServiceName));
        ctx.Provide(Dsh.WebBridge.WebBridgeRuntime.ApplicationServiceName, new Dsh.WebBridge.WebBridgeApplication());
        string? startedHome = null;
        var options = new Dsh.Boot.PluginEntrypointOptions(new Dsh.Boot.HarnessHome(root), root,
            StartLocalHost: (home, _) =>
            {
                startedHome = home;
                return Task.CompletedTask;
            },
            Arguments: [Dsh.WebBridge.WebBridgeCommand.PortOption, "0"]);
        try
        {
            var run = app.RunEntrypointAsync("web", options, ct);
            await WebSocketRpc.WaitForAsync(() => File.Exists(Dsh.WebBridge.WebBridgeEndpoint.PortFile(root)));
            Assert.Equal(root, startedHome);
            Assert.True(File.Exists(Dsh.WebBridge.WebBridgeEndpoint.TokenFile(root)));
            var port = int.Parse(await File.ReadAllTextAsync(Dsh.WebBridge.WebBridgeEndpoint.PortFile(root), ct),
                System.Globalization.CultureInfo.InvariantCulture);
            await activation.DeactivateAsync();
            Assert.Equal(0, await run);
            Assert.False(File.Exists(Dsh.WebBridge.WebBridgeEndpoint.PortFile(root)));
            Assert.False(File.Exists(Dsh.WebBridge.WebBridgeEndpoint.TokenFile(root)));
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            Assert.Null(ctx.Get(Dsh.WebBridge.WebBridgeRuntime.ServiceName, false));
            Assert.Throws<ObjectDisposedException>(() =>
            {
                _ = runtime.RunAsync(new Dsh.WebBridge.WebBridgeOptions(root, 0), (_, _) => Task.FromResult(0), ct);
            });
        }
        finally
        {
            await activation.DeactivateAsync();
            app.Dispose();
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void Register_DuplicatePackage_ThrowsAndKeepsFirst()
    {
        var host = new PluginHost();
        host.Catalog.Register(PluginDescriptor.For("dup/pkg", PluginForm.ManagedAssembly), () => new TestPlugin());

        var error = Assert.Throws<InvalidOperationException>(() =>
            host.Catalog.Register(PluginDescriptor.For("dup/pkg", PluginForm.NativeLibrary), () => new TestPlugin()));

        Assert.Contains("dup/pkg", error.Message);
        Assert.True(host.Catalog.TryDescribe("dup/pkg", out var descriptor));
        Assert.Equal(PluginForm.ManagedAssembly, descriptor.Form);
    }

    [Fact]
    public void Scan_LoadsPluginFromPerPackageSubdirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-scan-{Guid.NewGuid():N}");
        var pluginDir = Path.Combine(root, "plugins", "Dsh.Tests");
        Directory.CreateDirectory(pluginDir);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Dsh.Tests.dll"),
            Path.Combine(pluginDir, "Dsh.Tests.dll"));
        var weak = ScanPerPackageAndUnload(pluginDir);
        try
        {
            // Windows 上被装载的程序集持有内存映射(ERROR_ACCESS_DENIED),必须先回收 ALC 才能删目录。
            Assert.True(PluginUnloader.WaitForCollection(weak, out var report, maxRounds: 60), report);
        }
        finally
        {
            // ALC 回收后仍可能有加载器残留句柄(Windows 上被映射的 DLL 无法立即删除); 清理失败不应让用例失败。
            TempTree.Delete(root);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ScanPerPackageAndUnload(string pluginDir)
    {
        var host = new PluginHost();
        var result = host.Scan(Path.GetDirectoryName(pluginDir)!, null);
        Assert.Contains(result.Managed, plugin => plugin.Package == "test/local");
        Assert.DoesNotContain(result.Skipped, skip => skip.Reason.Contains("平铺"));
        return PluginUnloader.Unload(Assert.Single(result.Managed).Context);
    }

    [Fact]
    public void Scan_FlatLayoutStillLoadsButWarns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-scan-{Guid.NewGuid():N}");
        var pluginsDir = Path.Combine(root, "plugins");
        Directory.CreateDirectory(pluginsDir);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Dsh.Tests.dll"),
            Path.Combine(pluginsDir, "Dsh.Tests.dll"));
        var weak = ScanFlatAndUnload(pluginsDir);
        try
        {
            Assert.True(PluginUnloader.WaitForCollection(weak, out var report, maxRounds: 60), report);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ScanFlatAndUnload(string pluginsDir)
    {
        var host = new PluginHost();
        var result = host.Scan(pluginsDir, null);
        Assert.Contains(result.Managed, plugin => plugin.Package == "test/local");
        Assert.Contains(result.Skipped, skip => skip.Reason.Contains("平铺"));
        return PluginUnloader.Unload(Assert.Single(result.Managed).Context);
    }

    [Fact]
    public void SharedPool_LoadsOnceAndRejectsMajorVersionMismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-pool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var pool = new SharedAssemblyPool(root);
            var source = typeof(ToolRuntime).Assembly.Location;
            var name = typeof(ToolRuntime).Assembly.GetName();

            var first = pool.ResolveOrLoad(name, () => source);
            var second = pool.ResolveOrLoad(name, () => throw new InvalidOperationException("must not reload"));
            Assert.Same(first, second);

            var conflict = new AssemblyName(name.Name!) { Version = new Version(999, 0) };
            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                _ = pool.ResolveOrLoad(conflict, () => source);
            });
            Assert.Contains(name.Name!, error.Message);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }
}

public sealed record ThirdPartyNotification(string Text) : INotification
{
    public static string EventName => "third-party/notification";
}

[Collection(SerialProcessCollection.Name)]
public sealed class TestPlugin : IDshPlugin
{
    public static bool Applied { get; set; }
    public static string? ReceivedText { get; set; }

    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        Applied = true;
        ctx.On<ThirdPartyNotification>(notification => ReceivedText = notification.Text);
        ctx.Emit(new ThirdPartyNotification("third-party-hello"));
        return new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}


