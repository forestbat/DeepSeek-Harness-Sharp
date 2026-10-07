using Dsh.Gui.Services;
using Dsh.Tui;
using GpuAdapterInfo = Dsh.Tui.GpuAdapterInfo;
using GuiAdapterInfo = Dsh.Gui.Services.GpuAdapterInfo;

namespace Dsh.Tests;

/** GPU 设置的纯逻辑: Linux sysfs 解析、Windows PCI 位置解析、显卡候选匹配、后端标签。 */
public sealed class GpuPreferenceTests
{
    private const string AmdUevent = """
        DRIVER=amdgpu
        PCI_CLASS=30000
        PCI_ID=1002:15BF
        PCI_SLOT_NAME=0000:64:00.0
        MODALIAS=pci:v00001002d000015BFsv00001043sd00001EE1bc03sc00i00
        """;

    private const string NvidiaUevent = """
        DRIVER=nvidia
        PCI_CLASS=30200
        PCI_ID=10DE:28E0
        PCI_SLOT_NAME=0000:01:00.0
        """;

    private const string VirtioUevent = """
        DRIVER=virtio-pci
        PCI_ID=1AF4:1050
        PCI_SLOT_NAME=0000:00:01.0
        """;

    /** 真实注册表值的形态: 间接字符串是本地化的, 只有尾部 (总线,设备,功能) 元组稳定。 */
    private const string AmdLocation = @"@System32\drivers\pci.sys,#65536;PCI bus %1, device %2, function %3;(102,0,0)";

    [Fact]
    public void ParseLinuxUevent_ReadsDriverVendorAndSlot()
    {
        var amd = GpuCatalog.ParseLinuxUevent(AmdUevent);
        var nvidia = GpuCatalog.ParseLinuxUevent(NvidiaUevent);
        var virtio = GpuCatalog.ParseLinuxUevent(VirtioUevent);

        Assert.NotNull(amd);
        Assert.Equal("AMD", amd.Vendor);
        Assert.Equal("0000:64:00.0", amd.Id);
        Assert.Contains("amdgpu", amd.Name, StringComparison.Ordinal);
        Assert.Contains("1002:15BF", amd.Detail, StringComparison.Ordinal);

        Assert.NotNull(nvidia);
        Assert.Equal("NVIDIA", nvidia.Vendor);
        Assert.Equal("0000:01:00.0", nvidia.Id);

        Assert.NotNull(virtio);
        Assert.Equal("Virtio", virtio.Vendor);
    }

    [Fact]
    public void ParseLinuxUevent_ReturnsNull_WithoutDriverOrPciId()
    {
        Assert.Null(GpuCatalog.ParseLinuxUevent("DRIVER=amdgpu\n"));
        Assert.Null(GpuCatalog.ParseLinuxUevent("PCI_ID=1002:15BF\n"));
        Assert.Null(GpuCatalog.ParseLinuxUevent(""));
    }

    /** TUI 目录与 GUI 设置页各有一份独立实现, 两边都要按同一规则解析 Windows 的 PCI 位置。 */
    [Fact]
    public void ParsePciLocation_Reads_Trailing_Tuple_In_Both_Implementations()
    {
        Assert.Equal("0000:66:00.0", GpuCatalog.ParsePciLocation(AmdLocation));
        Assert.Equal("0000:01:00.0", GpuCatalog.ParsePciLocation("PCI bus 1, device 0, function 0 (1,0,0)"));
        Assert.Null(GpuCatalog.ParsePciLocation(null));
        Assert.Null(GpuCatalog.ParsePciLocation("PCI bus 1, device 0, function 0"));
        Assert.Null(GpuCatalog.ParsePciLocation("PCI 总线 1，设备 0，功能 0 (1,0)"));
        Assert.Null(GpuCatalog.ParsePciLocation("PCI bus x, device 0, function 0 (x,0,0)"));

        Assert.Equal("0000:66:00.0", GpuPreference.ParsePciLocation(AmdLocation));
        Assert.Equal("0000:01:00.0", GpuPreference.ParsePciLocation("PCI bus 1, device 0, function 0 (1,0,0)"));
        Assert.Null(GpuPreference.ParsePciLocation(null));
        Assert.Null(GpuPreference.ParsePciLocation("PCI bus 1, device 0, function 0"));
        Assert.Null(GpuPreference.ParsePciLocation("PCI 总线 1，设备 0，功能 0 (1,0)"));
        Assert.Null(GpuPreference.ParsePciLocation("PCI bus x, device 0, function 0 (x,0,0)"));
    }

    [Fact]
    public void SelectionIdOf_Prefers_Pci_Slot()
    {
        // 有 slot 用 slot(同名多卡唯一可区分), 拿不到 slot 才退回显示名。
        Assert.Equal("0000:66:00.0", GpuPreference.SelectionIdOf(new GuiAdapterInfo("0000:66:00.0", "AMD Radeon 780M Graphics", "AMD", "")));
        Assert.Equal("NVIDIA 显卡", GpuPreference.SelectionIdOf(new GuiAdapterInfo("0002", "NVIDIA 显卡", "NVIDIA", "")));
    }

    [Fact]
    public void MatchAdapterIndex_PrefersExactSubstring_ThenReverse()
    {
        string[] candidates = ["AMD Radeon 780M Graphics", "NVIDIA GeForce RTX 4060 Laptop GPU"];

        Assert.Equal(0, GpuCatalog.MatchAdapterIndex(candidates, GpuCatalog.AutoAdapter));
        Assert.Equal(0, GpuCatalog.MatchAdapterIndex(candidates, "AMD Radeon 780M"));
        Assert.Equal(1, GpuCatalog.MatchAdapterIndex(candidates, "NVIDIA GeForce RTX 4060 Laptop GPU"));
        Assert.Equal(1, GpuCatalog.MatchAdapterIndex(candidates, "RTX 4060"));
        // Avalonia 只报部分名字时也能反向匹配上。
        Assert.Equal(1, GpuCatalog.MatchAdapterIndex(["Intel", "4060"], "NVIDIA GeForce RTX 4060 Laptop GPU"));
        // 完全匹配不到时退回默认卡, 不抛异常。
        Assert.Equal(0, GpuCatalog.MatchAdapterIndex(candidates, "Intel Arc B580"));
        Assert.Equal(0, GpuCatalog.MatchAdapterIndex([], "NVIDIA"));
    }

    /** GUI 侧的同名消歧: 两张同型号卡时, 排序靠后的那张要对到第 2 个同名候选。 */
    [Fact]
    public void MatchAdapterIndex_Uses_SameName_Ordinal()
    {
        string[] candidates = ["NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA GeForce RTX 4060 Laptop GPU"];

        Assert.Equal(0, GpuPreference.MatchAdapterIndex(candidates, "NVIDIA GeForce RTX 4060 Laptop GPU"));
        Assert.Equal(1, GpuPreference.MatchAdapterIndex(candidates, "NVIDIA GeForce RTX 4060 Laptop GPU", sameNameOrdinal: 1));
        // 序号超出同名候选数时收敛到最后一个, 不越界。
        Assert.Equal(1, GpuPreference.MatchAdapterIndex(candidates, "NVIDIA GeForce RTX 4060 Laptop GPU", sameNameOrdinal: 5));
    }

    [Fact]
    public void SameNameOrdinal_Counts_Preceding_Names()
    {
        GuiAdapterInfo[] adapters =
        [
            new("0000:66:00.0", "AMD Radeon 780M Graphics", "AMD", ""),
            new("0000:01:00.0", "NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", ""),
            new("0000:02:00.0", "NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", ""),
        ];

        Assert.Equal(0, GpuPreference.SameNameOrdinal(adapters, 0));
        Assert.Equal(0, GpuPreference.SameNameOrdinal(adapters, 1));
        Assert.Equal(1, GpuPreference.SameNameOrdinal(adapters, 2));
        Assert.Equal(0, GpuPreference.SameNameOrdinal(adapters, -1));
    }

    [Fact]
    public void IsDiscrete_ClassifiesByUmaFlag()
    {
        Assert.True(GpuCatalog.IsDiscrete(new GpuAdapterInfo("0001", "NVIDIA GeForce RTX 4060", "NVIDIA", "", false)));
        Assert.True(GpuCatalog.IsDiscrete(new GpuAdapterInfo("0002", "AMD Radeon RX 7900 XTX", "AMD", "", false)));
        Assert.False(GpuCatalog.IsDiscrete(new GpuAdapterInfo("0000", "AMD Radeon 780M Graphics", "AMD", "", true)));
        Assert.False(GpuCatalog.IsDiscrete(new GpuAdapterInfo("0003", "Intel UHD Graphics 770", "Intel", "", true)));
        // 架构位未知时回退: NVIDIA 桌面卡判独显, 其余保守判核显。
        Assert.True(GpuCatalog.IsDiscrete(new GpuAdapterInfo("0004", "NVIDIA 显卡", "NVIDIA", "", null)));
        Assert.False(GpuCatalog.IsDiscrete(new GpuAdapterInfo("0005", "未知显卡", "未知", "", null)));
    }

    [Fact]
    public void BackendLabel_IsReadable()
    {
        Assert.Equal("软件渲染", GpuPreference.BackendLabel(GpuPreference.SoftwareBackend));
        Assert.Equal("Vulkan", GpuPreference.BackendLabel(GpuPreference.VulkanBackend));
        Assert.NotEqual(string.Empty, GpuPreference.BackendLabel(GpuPreference.AutoBackend));
    }

    [Fact]
    [Trait("Category", "OnlyGpu")]
    public void WindowsAdapterList_ContainsTheTwoRealCards_AndSkipsVirtualOnes()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var adapters = GpuCatalog.ListAdapters();

        Assert.NotEmpty(adapters);
        Assert.Contains(adapters, adapter => adapter.Vendor == "AMD");
        Assert.Contains(adapters, adapter => adapter.Vendor == "NVIDIA");
        Assert.DoesNotContain(adapters, adapter => adapter.Name.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase));
        // 本机架构位: 780M 核显共享内存(UMA), RTX 4060 独显自带显存(非 UMA)。NVIDIA 驱动处于 Code 43 错误态时 DXGI 不枚举、架构位为 null(该状态曾在 2026-09-17 真实发生), 此时此断言会失败。
        var amd = adapters.First(adapter => adapter.Vendor == "AMD");
        var nvidia = adapters.First(adapter => adapter.Vendor == "NVIDIA");
        Assert.True(amd.IsUma);
        Assert.False(nvidia.IsUma);
        // 选卡标识: PCI 卡用 slot(0000:bb:dd.f)而不是显示名, 同名多卡才区分得开; Detail 里也带 slot。
        Assert.True(GpuCatalog.LooksLikePciSlot(amd.Id), $"AMD 卡的 Id 不是 PCI slot: {amd.Id}");
        Assert.True(GpuCatalog.LooksLikePciSlot(nvidia.Id), $"NVIDIA 卡的 Id 不是 PCI slot: {nvidia.Id}");
        Assert.Contains(amd.Id, amd.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nvidia.Id, nvidia.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(amd.Id, nvidia.Id);
    }
}
