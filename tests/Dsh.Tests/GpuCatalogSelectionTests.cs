using Dsh.Boot;
using Dsh.Tui;
using Xunit;

namespace Dsh.Tests;

/**
 * Linux 选卡: 设置里的适配器值保存/读回, 以及"适配器 → /dev/dri/cardN"的 by-path 映射。
 * 无 DRM(Windows/WSL/容器)或没有 PCI slot 形态的适配器时跳过。
 */
public class GpuCatalogSelectionTests
{
    [Fact]
    public void Resolve_Returns_Null_For_Auto()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("选卡解析仅 Linux");
            return;
        }
        Assert.Null(GpuCatalog.ResolveDrmCardPath(GpuCatalog.AutoAdapter));
    }

    [Fact]
    public void Resolve_Returns_Null_For_Unknown_Slot()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("选卡解析仅 Linux");
            return;
        }
        Assert.Null(GpuCatalog.ResolveDrmCardPath("0000:ff:ff.0"));
    }

    [Fact]
    public void Saved_Slot_RoundTrips_And_Resolves_To_Card_Node()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("选卡解析仅 Linux");
            return;
        }
        var adapter = GpuCatalog.ListAdapters().FirstOrDefault(candidate => GpuCatalog.LooksLikePciSlot(candidate.Id));
        if (adapter is null)
        {
            Assert.Skip("本机没有 PCI slot 形态的 DRM 适配器");
            return;
        }

        var home = new HarnessHome(Path.Combine(AppContext.BaseDirectory, "gpu-selection-home", Guid.NewGuid().ToString("N")));
        try
        {
            GpuCatalog.SaveSelectedAdapter(home, adapter.Id);
            Assert.Equal(adapter.Id, GpuCatalog.LoadSelectedAdapter(home));

            var cardPath = GpuCatalog.ResolveDrmCardPath(adapter.Id);
            Assert.NotNull(cardPath);
            Assert.StartsWith("/dev/dri/card", cardPath, StringComparison.Ordinal);
            Assert.True(File.Exists(cardPath), $"解析出的卡节点不存在: {cardPath}");
        }
        finally
        {
            if (Directory.Exists(home.Root))
                Directory.Delete(home.Root, recursive: true);
        }
    }
}
