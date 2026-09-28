using System.Globalization;
using System.Runtime.Versioning;
using Dsh.Boot;
using Microsoft.Win32;

namespace Dsh.Tui;

public sealed record GpuAdapterInfo(string Id, string Name, string Vendor, string Detail, bool? IsUma);

/**
 * 显卡目录: 枚举本机显卡(Windows 显示类驱动注册表 / Linux /sys/class/drm)、按显存架构分辨核显(UMA)/独显、保存用户选卡。
 * 选卡持久化在 settings.yaml 的 plugins."@deepseek-ai/dsh-gui".gpu.adapter, GUI 设置页与 TUI /gpu 命令共用同一键, 重启进程后生效。
 */
public static class GpuCatalog
{
    public const string AutoAdapter = "auto";

    private const string GpuSettingsPackage = "@deepseek-ai/dsh-gui";
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string DriverDescValue = "DriverDesc";
    private const string MatchingDeviceIdValue = "MatchingDeviceId";
    private const string DrmRoot = "/sys/class/drm";
    private const string DrmDeviceRoot = "/dev/dri";

    /** 虚拟显示适配器(远程桌面/串流工具装出来的)不参与选择。 */
    private static readonly string[] VirtualAdapterTokens =
        ["virtual display", "gameviewer", "iddsample", "idd sample", "remote display", "basic render", "mirage", "meta virtual"];

    private static readonly Dictionary<string, string> VendorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1002"] = "AMD",
        ["1022"] = "AMD",
        ["10DE"] = "NVIDIA",
        ["8086"] = "Intel",
        ["15AD"] = "VMware",
        ["1AF4"] = "Virtio",
        ["1B36"] = "Red Hat",
    };

    /** 本机可用显卡: Windows 读注册表, Linux 读 sysfs; 拿不到就返回空列表, 绝不影响启动。 */
    public static IReadOnlyList<GpuAdapterInfo> ListAdapters()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ListWindowsAdapters();
            if (OperatingSystem.IsLinux())
                return ListLinuxAdapters();
        }
        catch (Exception)
        {
            return [];
        }
        return [];
    }

    /** 独显/核显分辨: 以显存架构为准——UMA(与 CPU 共享内存)为核显, 非 UMA(自带显存)为独显; 架构未知时 NVIDIA 桌面卡判独显, 其余保守判核显。 */
    public static bool IsDiscrete(GpuAdapterInfo adapter)
        => adapter.IsUma is { } uma ? !uma : adapter.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase);

    /** 候选描述列表里按名字挑用户选中的那张; 匹配不到就用默认(0)。 */
    public static int MatchAdapterIndex(IReadOnlyList<string> candidateDescriptions, string wanted)
    {
        if (candidateDescriptions.Count == 0 || wanted.Length == 0 || wanted.Equals(AutoAdapter, StringComparison.OrdinalIgnoreCase))
            return 0;
        for (var index = 0; index < candidateDescriptions.Count; index++)
        {
            if (candidateDescriptions[index].Contains(wanted, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        for (var index = 0; index < candidateDescriptions.Count; index++)
        {
            if (wanted.Contains(candidateDescriptions[index], StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return 0;
    }

    /** /sys/class/drm/cardN/device/uevent 的解析: 只关心 DRIVER / PCI_ID / PCI_SLOT_NAME。 */
    public static GpuAdapterInfo? ParseLinuxUevent(string uevent)
        => ParseLinuxUeventFull(uevent)?.Info;

    internal static (GpuAdapterInfo Info, string Driver, string? Slot)? ParseLinuxUeventFull(string uevent)
    {
        string? driver = null;
        string? pciId = null;
        string? slot = null;
        foreach (var line in uevent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("DRIVER=", StringComparison.Ordinal))
                driver = trimmed["DRIVER=".Length..];
            else if (trimmed.StartsWith("PCI_ID=", StringComparison.Ordinal))
                pciId = trimmed["PCI_ID=".Length..];
            else if (trimmed.StartsWith("PCI_SLOT_NAME=", StringComparison.Ordinal))
                slot = trimmed["PCI_SLOT_NAME=".Length..];
        }
        if (driver is null || pciId is null)
            return null;
        var vendorCode = pciId.Split(':')[0];
        var vendor = VendorNames.GetValueOrDefault(vendorCode, $"0x{vendorCode}");
        var name = $"{vendor} 显卡（{driver}）";
        var detail = $"{slot ?? "?"} · {pciId}";
        return (new GpuAdapterInfo(slot ?? pciId, name, vendor, detail, null), driver, slot);
    }

    /** 用户选中的显卡(设置值, auto 表示系统默认)。 */
    public static string LoadSelectedAdapter(HarnessHome home)
    {
        if (HarnessSettings.Load(home).Plugins.GetValueOrDefault(GpuSettingsPackage)?.Parameters is not { } parameters)
            return AutoAdapter;
        if (parameters.GetValueOrDefault("gpu") is not IReadOnlyDictionary<string, object?> gpu)
            return AutoAdapter;
        return gpu.GetValueOrDefault("adapter") as string is { Length: > 0 } adapter ? adapter : AutoAdapter;
    }

    /** 保存选卡, 保留该插件段里的其余参数(backend 等)。 */
    public static void SaveSelectedAdapter(HarnessHome home, string adapter)
    {
        var settings = HarnessSettings.Load(home);
        var existing = settings.Plugins.GetValueOrDefault(GpuSettingsPackage);
        var parameters = existing?.Parameters is { } current
            ? new Dictionary<string, object?>(current, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        var gpu = parameters.GetValueOrDefault("gpu") is IReadOnlyDictionary<string, object?> existingGpu
            ? new Dictionary<string, object?>(existingGpu, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        gpu["adapter"] = adapter;
        parameters["gpu"] = gpu;
        settings.Plugins[GpuSettingsPackage] = new PluginSetting
        {
            Enabled = existing?.Enabled ?? true,
            Parameters = parameters,
        };
        settings.SavePlugins(home);
    }

    /**
     * Linux 没有"进程内选卡"的公开 API: 用 PRIME 选择器表达偏好, 供 Mesa/NVIDIA 在创建 GL/Vulkan 设备时使用。
     * 这些变量必须在图形栈初始化前设置, 调用方要放在任何 GL 上下文创建之前。
     */
    [SupportedOSPlatform("linux")]
    public static void ApplyPrimeSelection(string preferred)
    {
        Environment.SetEnvironmentVariable("DRI_PRIME", null);
        Environment.SetEnvironmentVariable("__NV_PRIME_RENDER_OFFLOAD", null);
        Environment.SetEnvironmentVariable("__GLX_VENDOR_LIBRARY_NAME", null);
        if (preferred.Equals(AutoAdapter, StringComparison.OrdinalIgnoreCase))
            return;
        var adapter = FindLinuxAdapter(preferred);
        if (adapter is null)
            return;
        if (adapter.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("__NV_PRIME_RENDER_OFFLOAD", "1");
            Environment.SetEnvironmentVariable("__GLX_VENDOR_LIBRARY_NAME", "nvidia");
        }
        if (LooksLikePciSlot(adapter.Id))
            Environment.SetEnvironmentVariable("DRI_PRIME", $"pci-{adapter.Id}");
    }

    /**
     * 把设置里选的适配器解析成 GBM/KMS 要开的卡节点(/dev/dri/cardN)。
     * 选卡只记 PCI slot, 靠 by-path 符号链接映射到卡号; auto/匹配不到/无链接时返回 null(交给宿主自动扫描)。
     */
    [SupportedOSPlatform("linux")]
    public static string? ResolveDrmCardPath(string preferred)
    {
        if (preferred.Equals(AutoAdapter, StringComparison.OrdinalIgnoreCase))
            return null;
        var adapter = FindLinuxAdapter(preferred);
        if (adapter is null || !LooksLikePciSlot(adapter.Id))
            return null;
        var link = $"{DrmDeviceRoot}/by-path/pci-{adapter.Id}-card";
        try
        {
            if (!File.Exists(link))
                return null;
            return File.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /**
     * 设置里的选卡值 → 适配器: 先按 PCI slot 精确匹配(同名多卡只能这样区分),
     * 再按名字精确匹配, 最后按厂商名包含匹配。
     */
    [SupportedOSPlatform("linux")]
    private static GpuAdapterInfo? FindLinuxAdapter(string preferred)
    {
        var adapters = ListLinuxAdapters();
        if (LooksLikePciSlot(preferred))
            return adapters.FirstOrDefault(candidate => string.Equals(candidate.Id, preferred, StringComparison.OrdinalIgnoreCase));
        var matches = adapters.Where(adapter => preferred.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count > 0
            ? matches[0]
            : adapters.FirstOrDefault(candidate => preferred.Contains(candidate.Vendor, StringComparison.OrdinalIgnoreCase));
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<GpuAdapterInfo> ListWindowsAdapters()
    {
        using var baseKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (baseKey is null)
            return [];
        var adapters = new List<GpuAdapterInfo>();
        foreach (var name in baseKey.GetSubKeyNames())
        {
            if (!IsFourDigits(name))
                continue;
            using var subKey = baseKey.OpenSubKey(name);
            if (subKey?.GetValue(DriverDescValue) is not string driverDesc || driverDesc.Length == 0)
                continue;
            var deviceId = subKey.GetValue(MatchingDeviceIdValue) as string ?? "";
            var vendor = VendorOf(deviceId);
            adapters.Add(new GpuAdapterInfo(name, driverDesc, vendor, $"{vendor} · {ShortDeviceId(deviceId)}", ProbeWindowsUma(deviceId)));
        }
        return
        [
            .. adapters
                .Where(adapter => !IsVirtual(adapter.Name))
                .OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    [SupportedOSPlatform("linux")]
    private static IReadOnlyList<GpuAdapterInfo> ListLinuxAdapters()
    {
        if (!Directory.Exists(DrmRoot))
            return [];
        var adapters = new List<GpuAdapterInfo>();
        // 注意: .NET 的目录搜索模式只认 * 和 ?, 不支持 [0-9](实测 "card[0-9]*" 匹配 0 条), 只能全量枚举后按名过滤
        foreach (var card in Directory.EnumerateDirectories(DrmRoot))
        {
            if (!IsCardNodeName(Path.GetFileName(card)))
                continue;
            var ueventPath = Path.Combine(card, "device", "uevent");
            if (!File.Exists(ueventPath))
                continue;
            if (ParseLinuxUeventFull(File.ReadAllText(ueventPath)) is { } parsed)
            {
                var uma = GpuArchitectureProbe.QueryUmaLinux(Path.GetFileName(card), parsed.Driver, parsed.Slot);
                adapters.Add(parsed.Info with { IsUma = uma });
            }
        }
        return
        [
            .. adapters
                .OrderBy(adapter => adapter.Detail, StringComparer.OrdinalIgnoreCase)
                .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    [SupportedOSPlatform("windows")]
    private static bool? ProbeWindowsUma(string matchingDeviceId)
    {
        if (TokenAfter(matchingDeviceId, "VEN_") is not { } vendorHex || TokenAfter(matchingDeviceId, "DEV_") is not { } deviceHex)
            return null;
        if (!uint.TryParse(vendorHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vendorId)
            || !uint.TryParse(deviceHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var deviceId))
            return null;
        return GpuArchitectureProbe.QueryUmaWindows(vendorId, deviceId);
    }

    /**
     * 适配器 Id 是否为 PCI slot 形态(仅 Linux)。同名多卡(如集群里一批同型号)只有 slot 能区分,
     * 选卡值因此优先用 slot: 卡号(cardN)会随枚举次序变化, slot 不会。
     */
    public static bool LooksLikePciSlot(string id)
        => id.Count(character => character == ':') == 2 && id.Contains('.');

    /** 选卡值: Linux 用 PCI slot, 其余(Windows 注册表键等)用显示名。列表展示与落盘都走它, 保证同一套规则。 */
    public static string SelectionIdOf(GpuAdapterInfo adapter)
        => LooksLikePciSlot(adapter.Id) ? adapter.Id : adapter.Name;

    /** /sys/class/drm 或 /dev/dri 下的卡节点名: cardN(排除 cardN-输出名 与 renderD*)。 */
    internal static bool IsCardNodeName(string? name)
        => name is { Length: > 4 } && name.StartsWith("card", StringComparison.Ordinal) && name[4..].All(char.IsAsciiDigit);

    private static bool IsFourDigits(string value)
        => value.Length == 4 && value.All(char.IsAsciiDigit);

    private static bool IsVirtual(string name)
        => VirtualAdapterTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string VendorOf(string matchingDeviceId)
    {
        var vendorCode = TokenAfter(matchingDeviceId, "VEN_");
        return vendorCode is null ? "未知" : VendorNames.GetValueOrDefault(vendorCode, $"0x{vendorCode}");
    }

    private static string ShortDeviceId(string matchingDeviceId)
    {
        var vendor = TokenAfter(matchingDeviceId, "VEN_");
        var device = TokenAfter(matchingDeviceId, "DEV_");
        return vendor is null && device is null ? "未知设备" : $"VEN_{vendor ?? "?"}&DEV_{device ?? "?"}";
    }

    /** 取 `PREFIX` 后紧跟的四位十六进制编号(PCI 厂商/设备号)。 */
    private static string? TokenAfter(string text, string prefix)
    {
        var index = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || index + prefix.Length + 4 > text.Length)
            return null;
        var token = text.Substring(index + prefix.Length, 4);
        return token.All(Uri.IsHexDigit) ? token.ToUpperInvariant() : null;
    }
}
