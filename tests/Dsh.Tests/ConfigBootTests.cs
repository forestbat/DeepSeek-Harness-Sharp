using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;
using Dsh.Tools;

namespace Dsh.Tests;

public class ConfigBootTests
{
    [Fact]
    public async Task Compose_ActivatesConfiguredPluginsAndMergesDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-configboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        HarnessApp? app = null;
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            Directory.CreateDirectory(home.Root);
            await File.WriteAllTextAsync(Path.Combine(home.Root, "settings.yaml"), """
                global_default_model: deepseek-official/deepseek-v4-flash
                plugins:
                  "@deepseek-ai/dsh-core": true
                  "@deepseek-ai/dsh-interaction": true
                  "@deepseek-ai/dsh-persistence": true
                  "@deepseek-ai/dsh-subprocess": true
                  "@deepseek-ai/dsh-tool-bash": true
                  "@deepseek-ai/dsh-tool-fs": true
                  "@deepseek-ai/dsh-tool-fs-search":
                    sampleOverCapGlobResults: false
                  "@deepseek-ai/dsh-tool-todo":
                    allowParallelInProgress: true
                  "@deepseek-ai/dsh-fs-local": true
                  "@deepseek-ai/dsh-tool-str-replace-editor":
                    maxOutputChars: 16000
                """, TestContext.Current.CancellationToken);

            app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));

            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            var missing = new[] { "bash", "read", "write", "edit", "glob", "grep", "todo_write", "str_replace_editor" }
                .Where(name => tools.Get(name) is null)
                .ToList();
            Assert.True(missing.Count == 0, "missing tools: " + string.Join(", ", missing));

            Assert.NotNull(app.Ctx.Get<LocalFsService>(LocalFsService.ServiceName));

            // 用户 settings.yaml 未列出的插件发现即启用
            Assert.Equal(ActivationState.Active, app.Composition!.Find("@deepseek-ai/dsh-checkpoints")?.State);
            Assert.Equal(ActivationState.Active, app.Composition.Find("@deepseek-ai/dsh-ide-history")?.State);

            var fsSearch = app.Composition.Find("@deepseek-ai/dsh-tool-fs-search")!;
            Assert.Equal(false, (fsSearch.Config as IDictionary<string, object?>)?["sampleOverCapGlobResults"]);
        }
        finally
        {
            app?.Dispose();
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
    public async Task Compose_SkipsDisabledPlugins()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-configboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        HarnessApp? app = null;
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            Directory.CreateDirectory(home.Root);
            await File.WriteAllTextAsync(Path.Combine(home.Root, "settings.yaml"), """
                plugins:
                  "@deepseek-ai/dsh-tool-todo": false
                """, TestContext.Current.CancellationToken);

            app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));

            Assert.Null(app.Composition!.Find("@deepseek-ai/dsh-tool-todo"));
            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            Assert.Null(tools.Get("todo_write"));
            Assert.NotNull(tools.Get("bash"));
        }
        finally
        {
            app?.Dispose();
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
