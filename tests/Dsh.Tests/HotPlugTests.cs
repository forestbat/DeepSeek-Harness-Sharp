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
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));
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
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));
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
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));
            var manager = app.Ctx.Get<HarnessPluginManager>("pluginManager")!;
            var pluginPath = Path.Combine(AppContext.BaseDirectory, "Dsh.Tests.dll");
            Assert.True(File.Exists(pluginPath), $"plugin assembly not found: {pluginPath}");
            // 测试程序集本身在镜像内;禁用它之后,再作为托管程序集从文件装入可回收 ALC。
            Assert.Equal(ActivationState.Active, app.Composition!.Find("test/local")!.State);
            Assert.Contains("removed", await manager.RemoveAsync("test/local"));
            Assert.Null(app.Composition.Find("test/local"));

            var added = await manager.AddAsync(pluginPath);

            Assert.Contains("activated", added);
            Assert.Equal(ActivationState.Active, app.Composition.Find("test/local")!.State);
            Assert.StartsWith("active [managed-assembly", manager.Describe("test/local"));

            var removed = await manager.RemoveAsync("test/local");

            Assert.Contains("removed", removed);
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
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));
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
