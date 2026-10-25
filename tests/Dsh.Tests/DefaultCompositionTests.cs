using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class DefaultCompositionTests
{

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
