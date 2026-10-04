using Dsh.Boot;
using Dsh.Tui;

namespace Dsh.Tests;

/**
 * 选卡: 设置值的保存/读回、候选匹配、Linux 的"适配器 → /dev/dri/cardN"by-path 映射、Windows 的进程级偏好。
 * 适配器枚举 Windows(注册表)/Linux(sysfs)各一套, 纯逻辑跨平台, 平台专属断言按 OS 分支, 不再整条测试跳过。
 */
public class GpuCatalogSelectionTests
{
    [Fact]
    public void SelectionLabels_Start_With_Auto_And_Cover_Every_Adapter()
    {
        var labels = GpuCatalog.SelectionLabels();

        Assert.Contains("auto", labels[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GpuCatalog.ListAdapters().Count + 1, labels.Count);
    }

    [Fact]
    public void WindowsPreferenceData_Maps_Discrete_To_HighPerformance()
    {
        Assert.Equal("GpuPreference=2;", GpuCatalog.WindowsPreferenceData(discrete: true));
        Assert.Equal("GpuPreference=1;", GpuCatalog.WindowsPreferenceData(discrete: false));
    }

    /** auto 是默认值, 不强制任何卡; Linux 的 DRM 解析对 auto 直接返回 null。 */
    [Fact]
    public void Auto_Selection_Is_Default_And_Forces_No_Card()
    {
        using var home = new TempHome();
        Assert.Equal(GpuCatalog.AutoAdapter, GpuCatalog.LoadSelectedAdapter(home.Home));
        Assert.Equal(0, GpuCatalog.MatchAdapterIndex(GpuCatalog.SelectionLabels(), GpuCatalog.AutoAdapter));
        if (OperatingSystem.IsLinux())
            Assert.Null(GpuCatalog.ResolveDrmCardPath(GpuCatalog.AutoAdapter));
    }

    /** 未知选卡值退化为默认(auto), 不强制任何卡; Linux 的 DRM 解析找不到适配器时返回 null。 */
    [Fact]
    public void Unknown_Selection_Forces_No_Card()
    {
        Assert.Equal(0, GpuCatalog.MatchAdapterIndex(GpuCatalog.SelectionLabels(), "0000:ff:ff.0"));
        if (OperatingSystem.IsLinux())
            Assert.Null(GpuCatalog.ResolveDrmCardPath("0000:ff:ff.0"));
    }

    /** 选卡值(PCI slot / 显示名)在 Windows 与 Linux 上都能保存并读回; Linux 有 slot 时再解析到具体卡节点。 */
    [Fact]
    public void Saved_Selection_RoundTrips_And_Resolves_To_Card_Node()
    {
        var adapter = GpuCatalog.ListAdapters().FirstOrDefault();
        if (adapter is null)
        {
            Assert.Skip("本机没有可用显卡");
            return;
        }

        using var home = new TempHome();
        var selection = GpuCatalog.SelectionIdOf(adapter);
        GpuCatalog.SaveSelectedAdapter(home.Home, selection);
        Assert.Equal(selection, GpuCatalog.LoadSelectedAdapter(home.Home));

        if (!OperatingSystem.IsLinux() || !GpuCatalog.LooksLikePciSlot(adapter.Id))
            return;
        var cardPath = GpuCatalog.ResolveDrmCardPath(selection);
        Assert.NotNull(cardPath);
        Assert.StartsWith("/dev/dri/card", cardPath, StringComparison.Ordinal);
        Assert.True(File.Exists(cardPath), $"解析出的卡节点不存在: {cardPath}");
    }

    private sealed class TempHome : IDisposable
    {
        public HarnessHome Home { get; } = new(Path.Combine(
            AppContext.BaseDirectory,
            "gpu-selection-home",
            Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            if (Directory.Exists(Home.Root))
                Directory.Delete(Home.Root, recursive: true);
        }
    }
}
