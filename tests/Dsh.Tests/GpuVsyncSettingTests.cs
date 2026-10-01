using Dsh.Boot;
using Dsh.Tui;
using Xunit;

namespace Dsh.Tests;

/** 独立窗口(GPU)形态的垂直同步开关来自 settings.yaml: plugins."@deepseek-ai/dsh-gui".gpu.vsync, 缺省 true。 */
public class GpuVsyncSettingTests
{
    [Fact]
    public void Vsync_Defaults_To_On_And_Comes_From_Settings()
    {
        var home = new HarnessHome(Path.Combine(AppContext.BaseDirectory, "gpu-vsync-home", Guid.NewGuid().ToString("N")));
        try
        {
            Directory.CreateDirectory(home.Root);
            var settings = Path.Combine(home.Root, "settings.yaml");

            // 完全没有配置 → 默认开(不写死成关)
            File.WriteAllText(settings, "global_default_model: x\nplugins: {}\n");
            Assert.True(GpuCatalog.LoadVsync(home));

            // 有 GUI 插件段但没有 vsync → 默认开
            File.WriteAllText(settings, "plugins:\n  \"@deepseek-ai/dsh-gui\":\n    gpu: { adapter: auto }\n");
            Assert.True(GpuCatalog.LoadVsync(home));

            File.WriteAllText(settings, "plugins:\n  \"@deepseek-ai/dsh-gui\":\n    gpu: { vsync: true }\n");
            Assert.True(GpuCatalog.LoadVsync(home));

            File.WriteAllText(settings, "plugins:\n  \"@deepseek-ai/dsh-gui\":\n    gpu: { vsync: false }\n");
            Assert.False(GpuCatalog.LoadVsync(home));
        }
        finally
        {
            if (Directory.Exists(home.Root))
                Directory.Delete(home.Root, recursive: true);
        }
    }
}
