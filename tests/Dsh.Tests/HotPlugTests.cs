using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class HotPlugTests
{
    [Fact]
    public async Task PluginManager_RemovesAndReactivatesPlugin()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-hotplug-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir, PluginsDirectory: Path.Combine(dir, "plugins")));
            var manager = app.Ctx.Get<HarnessPluginManager>("pluginManager")!;
            Assert.Contains("@deepseek-ai/dsh-tool-todo", manager.PackageNames);
            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            Assert.NotNull(tools.Get("todo_write"));

            var removed = await manager.RemoveAsync("@deepseek-ai/dsh-tool-todo");
            Assert.Contains("removed", removed);
            Assert.Null(tools.Get("todo_write"));
            Assert.Null(app.Composition!.Find("@deepseek-ai/dsh-tool-todo"));
            Assert.Contains("\"@deepseek-ai/dsh-tool-todo\": false", ReadSettings(home));

            var added = await manager.AddAsync("@deepseek-ai/dsh-tool-todo");
            Assert.Contains("activated", added);
            Assert.NotNull(tools.Get("todo_write"));
            Assert.Equal(ActivationState.Active, app.Composition.Find("@deepseek-ai/dsh-tool-todo")!.State);
            Assert.Contains("\"@deepseek-ai/dsh-tool-todo\": true", ReadSettings(home));

            var disabled = await manager.DisableAsync("@deepseek-ai/dsh-tool-todo");
            Assert.Contains("disabled", disabled);
            Assert.Null(app.Composition.Find("@deepseek-ai/dsh-tool-todo"));
            Assert.Contains("\"@deepseek-ai/dsh-tool-todo\": false", ReadSettings(home));

            var enabled = await manager.EnableAsync("@deepseek-ai/dsh-tool-todo");
            Assert.Contains("activated", enabled);
            Assert.NotNull(tools.Get("todo_write"));
            Assert.Contains("\"@deepseek-ai/dsh-tool-todo\": true", ReadSettings(home));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task PluginManager_ReportsUnknownPackage()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-hotplug-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir, PluginsDirectory: Path.Combine(dir, "plugins")));
            var manager = app.Ctx.Get<HarnessPluginManager>("pluginManager")!;
            Assert.Contains("not found", await manager.AddAsync("@deepseek-ai/dsh-missing"));
            Assert.Contains("not active", await manager.RemoveAsync("@deepseek-ai/dsh-missing"));
            Assert.Contains("not found", await manager.EnableAsync("@deepseek-ai/dsh-missing"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task PluginManager_AddsDynamicAssemblyAndCollectsItOnRemove()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-hotplug-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir, PluginsDirectory: Path.Combine(dir, "plugins")));
            var manager = app.Ctx.Get<HarnessPluginManager>("pluginManager")!;
            var pluginPath = Path.Combine(AppContext.BaseDirectory, "Dsh.ManagedPluginSample.dll");
            Assert.True(File.Exists(pluginPath), $"plugin assembly not found: {pluginPath}");
            // 夹具的产物在测试输出根目录, 不在 plugins/ 下, 所以不会被组合期的扫描发现;
            // 这里禁掉镜像内的同名包, 再把它作为托管程序集从文件装入可回收 ALC。
            Assert.Equal(ActivationState.Active, app.Composition!.Find("test/local")!.State);
            Assert.Contains("removed", await manager.RemoveAsync("test/local"));
            Assert.Null(app.Composition.Find("test/local"));

            var added = await manager.AddAsync(pluginPath);

            Assert.Contains("activated", added);
            Assert.Equal(ActivationState.Active, app.Composition.Find("test/local")!.State);
            Assert.StartsWith("active [managed-assembly", manager.Describe("test/local"));
            var installDir = Path.Combine(dir, "plugins", "Dsh.ManagedPluginSample");
            Assert.True(Directory.Exists(installDir), $"add 应把插件落盘到: {installDir}");

            var removed = await manager.RemoveAsync("test/local");

            Assert.Contains("removed", removed);
            Assert.Contains("已删除安装目录", removed);
            Assert.False(Directory.Exists(installDir), $"remove 应删除安装目录: {installDir}");
            Assert.DoesNotContain("could not be collected", removed);
            Assert.Null(app.Composition.Find("test/local"));
            for (var attempt = 0; attempt < 30; attempt++)
            {
                Assert.DoesNotContain("leaked", manager.Describe("test/local"));
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            // 安装根就在这个临时目录里, 随它一起删掉, 不碰测试输出目录。
            DeleteBestEffort(dir);
        }
    }

    private static void DeleteBestEffort(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task PluginManager_WritebackKeepsCommentsAndOtherSections()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-hotplug-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            Directory.CreateDirectory(home.Root);
            await File.WriteAllTextAsync(Path.Combine(home.Root, "settings.yaml"), """
                rules: []

                # 用户注释:不要删我
                plugins:
                  "@deepseek-ai/dsh-tool-todo": false

                safety:
                  autoApprove: false
                """, TestContext.Current.CancellationToken);
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir, PluginsDirectory: Path.Combine(dir, "plugins")));
            var manager = app.Ctx.Get<HarnessPluginManager>("pluginManager")!;
            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            Assert.Null(tools.Get("todo_write"));

            var enabled = await manager.EnableAsync("@deepseek-ai/dsh-tool-todo");

            Assert.Contains("activated", enabled);
            Assert.NotNull(tools.Get("todo_write"));
            var settings = ReadSettings(home);
            Assert.Contains("# 用户注释:不要删我", settings);
            Assert.Contains("safety:", settings);
            Assert.Contains("autoApprove: false", settings);
            Assert.Contains("\"@deepseek-ai/dsh-tool-todo\": true", settings);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string ReadSettings(HarnessHome home)
        => File.ReadAllText(Path.Combine(home.Root, "settings.yaml"));
}
