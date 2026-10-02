using Dsh.Plugins;

namespace Dsh.Tests;

public sealed class PluginDiscoveryTests
{
    [Fact]
    public void Scan_RegistersManagedPluginFromFolderAsManagedAssembly()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Dsh.Goal.dll");
        Assert.True(File.Exists(source), $"missing build output: {source}");
        var folder = Path.Combine(Path.GetTempPath(), $"dsh-plugin-folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            ScanAndUnload(source, folder);
        }
        finally
        {
            DeletePluginFolder(folder);
        }
    }

    /** 扫描与断言独立成方法: 返回后 host/catalog 的强引用随栈帧消失, ALC 才可能被回收。 */
    private static void ScanAndUnload(string source, string folder)
    {
        // 每插件一个目录是规范布局:目录名只是发现入口,包名以清单为准。
        var pluginDir = Path.Combine(folder, "Dsh.Goal");
        Directory.CreateDirectory(pluginDir);
        File.Copy(source, Path.Combine(pluginDir, "Dsh.Goal.dll"));
        var host = new PluginHost();

        var result = host.Scan(folder, nativeBridge: null);

        Assert.Empty(result.Skipped);
        Assert.Contains(result.Managed, entry => entry.Package == "@deepseek-ai/dsh-goal");
        Assert.True(host.Catalog.TryDescribe("@deepseek-ai/dsh-goal", out var descriptor));
        Assert.Equal(PluginForm.ManagedAssembly, descriptor.Form);
        Assert.True(descriptor.Capabilities.HasFlag(PluginCapabilities.Unload));
        Assert.True(host.Catalog.TryCreateDefinition("@deepseek-ai/dsh-goal", out var definition));
        Assert.NotNull(definition);
        foreach (var entry in result.Managed)
            entry.Context.Unload();
    }

    /** Windows 上已装载的程序集要等 ALC 真正回收后才能删除;卸载是异步的,这里等它一小会儿。 */
    private static void DeletePluginFolder(string folder)
    {
        for (var attempt = 0; ; attempt += 1)
        {
            try
            {
                Directory.Delete(folder, true);
                return;
            }
            catch (Exception error) when (attempt < 50 && error is IOException or UnauthorizedAccessException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
        }
    }
}
