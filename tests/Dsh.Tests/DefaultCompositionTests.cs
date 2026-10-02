using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class DefaultCompositionTests
{
    [Fact]
    public async Task DiscoveredPlugins_ActivateAndRegisterIdeHistoryTool()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dsh-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(directory, "home"));
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: directory));
            var composition = app.Composition!;
            // memory 与 checkpoints 默认禁用(见默认 settings 模板),打开后才参与组合。
            Assert.Null(composition.Find("@deepseek-ai/dsh-checkpoints"));
            Assert.Null(composition.Find("@deepseek-ai/dsh-memory"));
            var unhealthy = composition.Activations
                .Where(activation => activation.State == ActivationState.Failed)
                .Select(activation => $"{activation.Name}={activation.State}:{activation.Error}")
                .ToList();
            Assert.True(unhealthy.Count == 0, string.Join(" | ", unhealthy));
            Assert.Equal(ActivationState.Active, composition.Find("@deepseek-ai/dsh-ide-history")?.State);
            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName);
            Assert.NotNull(tools);
            Assert.NotNull(tools.Get("ide_history"));
            Assert.NotNull(tools.Get("todo_write"));
            Assert.Equal(ActivationState.Active, composition.Find("@deepseek-ai/dsh-goal")?.State);
            Assert.Equal(ActivationState.Active, composition.Find("@deepseek-ai/dsh-tool-goal")?.State);
            Assert.Equal(ActivationState.Active, composition.Find("@deepseek-ai/dsh-tool-web")?.State);
            Assert.NotNull(tools.Get("web_search"));
            Assert.NotNull(tools.Get("web_fetch"));
            Assert.Equal(ActivationState.Active, composition.Find("@deepseek-ai/dsh-telemetry")?.State);
            Assert.NotNull(app.Ctx.Get<Dsh.Telemetry.TelemetryService>(Dsh.Telemetry.TelemetryService.ServiceName, false));
            Assert.Equal(ActivationState.Active, composition.Find("@deepseek-ai/dsh-tool-session-query")?.State);
            Assert.NotNull(tools.Get("session_search"));
            Assert.NotNull(app.Ctx.Get<Dsh.SessionQuery.SessionQueryService>(Dsh.SessionQuery.SessionQueryService.ServiceName, false));

            var settings = File.ReadAllText(Path.Combine(home.Root, "settings.yaml"));
            Assert.Contains("plugins:", settings);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task EntrypointPlugin_ActivatesAlongsideDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dsh-entrypoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(directory, "home"));
            using var app = await ConfigBoot.Compose(new HarnessOptions(
                home,
                Cwd: directory,
                IsTui: true));
            Assert.Equal(ActivationState.Active, app.Composition!.Find("@deepseek-ai/dsh-tui")?.State);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task DisabledEntrypointPlugin_StaysInactive()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dsh-entrypoint-off-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(directory, "home"));
            var settings = HarnessSettings.Load(home);
            settings.Plugins["@deepseek-ai/dsh-tui"] = new PluginSetting { Enabled = false };
            settings.SavePlugins(home);
            using var app = await ConfigBoot.Compose(new HarnessOptions(
                home,
                Cwd: directory,
                IsTui: true));
            Assert.Null(app.Composition!.Find("@deepseek-ai/dsh-tui"));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
