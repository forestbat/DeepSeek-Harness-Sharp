using System.Reflection;
using System.Runtime.CompilerServices;
using Dsh.Core;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Plugins;

[assembly: DshPlugin("test/local")]

namespace Dsh.Tests;

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

        // 入口描述符随插件清单生成:丢掉接线(如 Web/Lsp 在重构中丢过)会在这里暴露。
        Assert.Contains(host.Catalog.Descriptors, entry => entry.Entry == "web");
        Assert.Contains(host.Catalog.Descriptors, entry => entry.Entry == "lsp");
        Assert.Contains(host.Catalog.Descriptors, entry => entry.Package == "@deepseek-ai/dsh-tool-web");

        var ctx = new Context();
        var activation = ctx.Plugin(definition!);
        await activation.WaitAsync();
        Assert.True(TestPlugin.Applied);
        Assert.Equal(ActivationState.Active, activation.State);
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
            // ALC 回收后仍可能有其他加载器残留句柄, 删除失败不应让用例失败(临时目录由系统清理)
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
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
            Directory.Delete(root, true);
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
            Directory.Delete(root, true);
        }
    }
}

public sealed record ThirdPartyNotification(string Text) : INotification
{
    public static string EventName => "third-party/notification";
}

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
