using System.Globalization;
using System.Runtime.Versioning;
using Avalonia;
using Microsoft.Win32;

namespace Dsh.Gui.Services;

public sealed record GpuAdapterInfo(string Id, string Name, string Vendor, string Detail);

/**
 * 图形设置: 主选项是「用哪张显卡」, 渲染后端只是高级项。
 * 选卡标识用 PCI slot(同名多卡唯一可区分): Linux 取 sysfs 的 PCI_SLOT_NAME, Windows 由显示类子键回查 Enum\PCI 的设备实例,
 * 拿不到 slot 时退回设备实例路径(设备实例 ID), 再退回注册表键名与显示名。
 * 这里与 Dsh.Tui.GpuCatalog 是有意各自独立的两份实现, 改选卡规则时两边都要动, 保持规则一致。
 * Windows: 显卡列表读显示类驱动注册表(DriverDesc + MatchingDeviceId), 选择通过 Avalonia 的显卡选择回调按名字匹配(同名多卡按同名序号消歧);
 * Linux: 显卡列表读 /sys/class/drm, 选择通过 PRIME 环境变量(DRI_PRIME / __NV_PRIME_RENDER_OFFLOAD)在启动时生效。
 */
public static class GpuPreference
{
    public const string AutoAdapter = "auto";
    public const string SoftwareBackend = "software";
    public const string AutoBackend = "auto";
    public const string OpenGlBackend = "opengl";
    public const string VulkanBackend = "vulkan";

    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\" + DisplayClassGuid;
    private const string DriverDescValue = "DriverDesc";
    private const string MatchingDeviceIdValue = "MatchingDeviceId";
    private const string PciEnumKey = @"SYSTEM\CurrentControlSet\Enum\PCI";
    private const string DeviceDriverValue = "Driver";
    private const string DeviceLocationValue = "LocationInformation";
    private const string PciDomain = "0000";
    private const int LocationParts = 3;
    private const string DrmRoot = "/sys/class/drm";
    private const string EglBackend = "egl";
    private const string SoftwareLabel = "软件渲染";
    private const string DefaultLabel = "默认后端";

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

    private static string? _activeBackend;
    private static string? _activeAdapter;

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

    /** 当前生效的后端与显卡。 */
    public static string DescribeCurrent()
        => _activeBackend is null ? DefaultLabel : _activeAdapter is null ? _activeBackend : $"{_activeBackend} · {_activeAdapter}";

    /** 供设置页显示: 后端标签翻译。 */
    public static string BackendLabel(string backend)
        => backend switch
        {
            SoftwareBackend => SoftwareLabel,
            VulkanBackend => "Vulkan",
            EglBackend => OperatingSystem.IsWindows() ? "ANGLE/Egl" : "EGL",
            OpenGlBackend => OperatingSystem.IsWindows() ? "WGL/OpenGL" : "GLX",
            _ => OperatingSystem.IsWindows() ? "ANGLE/Egl（自动）" : "GLX（自动）",
        };

    public static void Apply(AppBuilder builder, GuiSettingsSnapshot settings)
    {
        var backend = settings.GpuEnabled ? Normalize(settings.GpuBackend) : SoftwareBackend;
        var preferred = settings.GpuAdapter.Trim();
        if (OperatingSystem.IsWindows())
        {
            ApplyWindows(builder, backend, preferred);
            _activeBackend = BackendLabel(backend);
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            ApplyLinux(builder, backend, preferred);
            _activeBackend = BackendLabel(backend);
        }
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
            var identity = WindowsDeviceIdentity(name);
            var vendor = VendorOf(deviceId);
            var id = identity.PciSlot ?? identity.DeviceInstancePath ?? name;
            adapters.Add(new GpuAdapterInfo(id, driverDesc, vendor, $"{vendor} · {identity.PciSlot ?? ShortDeviceId(deviceId)}"));
        }
        return
        [
            .. adapters
                .Where(adapter => !IsVirtual(adapter.Name))
                .OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /** Windows LocationInformation → PCI slot: 只认尾部括号里的 (总线,设备,功能) 三元组, 格式串随系统语言变, 元组不变。 */
    public static string? ParsePciLocation(string? locationInformation)
    {
        if (locationInformation is null)
            return null;
        var open = locationInformation.LastIndexOf('(');
        var close = locationInformation.LastIndexOf(')');
        if (open < 0 || close <= open)
            return null;
        var parts = locationInformation[(open + 1)..close].Split(',');
        if (parts.Length != LocationParts)
            return null;
        if (!TryParseLocationPart(parts[0], out var bus)
            || !TryParseLocationPart(parts[1], out var device)
            || !TryParseLocationPart(parts[2], out var function))
            return null;
        return $"{PciDomain}:{bus:X2}:{device:X2}.{function}";
    }

    /**
     * 显示类驱动子键("0000") → (PCI slot, 设备实例路径)。
     * Enum\PCI 下每个设备实例的 Driver 值回指它的显示类子键, 这是子键与物理设备之间唯一可靠的链接;
     * 非 PCI 设备(虚拟显示适配器)查不到, 返回 (null, null)。
     */
    [SupportedOSPlatform("windows")]
    private static (string? PciSlot, string? DeviceInstancePath) WindowsDeviceIdentity(string classSubKeyName)
    {
        using var root = Registry.LocalMachine.OpenSubKey(PciEnumKey);
        if (root is null)
            return (null, null);
        var expectedDriver = $@"{DisplayClassGuid}\{classSubKeyName}";
        foreach (var hardwareId in root.GetSubKeyNames())
        {
            using var hardwareKey = root.OpenSubKey(hardwareId);
            if (hardwareKey is null)
                continue;
            foreach (var instanceId in hardwareKey.GetSubKeyNames())
            {
                using var instanceKey = hardwareKey.OpenSubKey(instanceId);
                if (instanceKey?.GetValue(DeviceDriverValue) is not string driver
                    || !driver.Equals(expectedDriver, StringComparison.OrdinalIgnoreCase))
                    continue;
                return (ParsePciLocation(instanceKey.GetValue(DeviceLocationValue) as string), $@"PCI\{hardwareId}\{instanceId}");
            }
        }
        return (null, null);
    }

    private static bool TryParseLocationPart(string text, out int value)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;

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
            if (ParseLinuxUevent(File.ReadAllText(ueventPath)) is { } adapter)
                adapters.Add(adapter);
        }
        return
        [
            .. adapters
                .OrderBy(adapter => adapter.Detail, StringComparer.OrdinalIgnoreCase)
                .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /** /sys/class/drm/cardN/device/uevent 的解析: 只关心 DRIVER / PCI_ID / PCI_SLOT_NAME。 */
    public static GpuAdapterInfo? ParseLinuxUevent(string uevent)
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
        return new GpuAdapterInfo(slot ?? pciId, name, vendor, detail);
    }

    /**
     * Avalonia 给出的候选里按名字挑用户选中的那张: 正向(候选含选卡值)优先, 再反向(选卡值含候选);
     * 同名多卡按 sameNameOrdinal 取第 N 个; 匹配不到就用默认(0)。
     */
    public static int MatchAdapterIndex(IReadOnlyList<string> candidateDescriptions, string wanted, int sameNameOrdinal = 0)
    {
        if (candidateDescriptions.Count == 0 || wanted.Length == 0 || wanted.Equals(AutoAdapter, StringComparison.OrdinalIgnoreCase))
            return 0;
        var forward = Matches(candidateDescriptions, description => description.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        if (forward.Count > 0)
            return forward[Math.Min(sameNameOrdinal, forward.Count - 1)];
        var reverse = Matches(candidateDescriptions, description => wanted.Contains(description, StringComparison.OrdinalIgnoreCase));
        return reverse.Count > 0 ? reverse[Math.Min(sameNameOrdinal, reverse.Count - 1)] : 0;
    }

    private static List<int> Matches(IReadOnlyList<string> candidates, Func<string, bool> predicate)
    {
        var found = new List<int>();
        for (var index = 0; index < candidates.Count; index++)
        {
            if (predicate(candidates[index]))
                found.Add(index);
        }
        return found;
    }

    /** 选卡值: 有 PCI slot 用 slot(同名多卡唯一可区分), 否则退回显示名。列表展示与落盘都走它。 */
    public static string SelectionIdOf(GpuAdapterInfo adapter)
        => LooksLikePciSlot(adapter.Id) ? adapter.Id : adapter.Name;

    /**
     * 选卡值 → 适配器下标: 先按 PCI slot 精确匹配(同名多卡只能这样区分),
     * 再按名字精确匹配(兼容早期只存显示名的设置), 最后按厂商名包含匹配; 匹配不到为 -1。
     */
    public static int IndexOfSelection(IReadOnlyList<GpuAdapterInfo> adapters, string preferred)
    {
        if (preferred.Length == 0 || preferred.Equals(AutoAdapter, StringComparison.OrdinalIgnoreCase))
            return -1;
        if (LooksLikePciSlot(preferred))
            return IndexOfMatch(adapters, adapter => adapter.Id.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        var byName = IndexOfMatch(adapters, adapter => adapter.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        return byName >= 0
            ? byName
            : IndexOfMatch(adapters, adapter => preferred.Contains(adapter.Vendor, StringComparison.OrdinalIgnoreCase));
    }

    /** 选卡值 → 适配器; 匹配不到为 null。 */
    public static GpuAdapterInfo? FindAdapter(string preferred)
    {
        var adapters = ListAdapters();
        var index = IndexOfSelection(adapters, preferred);
        return index >= 0 ? adapters[index] : null;
    }

    /** 同名适配器里的序号(0 起): Avalonia 候选里第 N 个同名项才对得上用户选的那张。 */
    public static int SameNameOrdinal(IReadOnlyList<GpuAdapterInfo> adapters, int index)
    {
        if (index < 0 || index >= adapters.Count)
            return 0;
        var ordinal = 0;
        for (var candidate = 0; candidate < index; candidate++)
        {
            if (adapters[candidate].Name.Equals(adapters[index].Name, StringComparison.OrdinalIgnoreCase))
                ordinal++;
        }
        return ordinal;
    }

    private static int IndexOfMatch(IReadOnlyList<GpuAdapterInfo> adapters, Func<GpuAdapterInfo, bool> predicate)
    {
        for (var index = 0; index < adapters.Count; index++)
        {
            if (predicate(adapters[index]))
                return index;
        }
        return -1;
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(AppBuilder builder, string backend, string preferred)
    {
        IReadOnlyList<Win32RenderingMode> modes = backend switch
        {
            SoftwareBackend => [Win32RenderingMode.Software],
            OpenGlBackend => [Win32RenderingMode.Wgl, Win32RenderingMode.Software],
            VulkanBackend => [Win32RenderingMode.Vulkan, Win32RenderingMode.Software],
            _ => [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
        };
        var options = new Win32PlatformOptions { RenderingMode = modes };
        options.GraphicsAdapterSelectionCallback = candidates =>
        {
            if (candidates.Count == 0)
                return 0;
            var descriptions = candidates.Select(candidate => candidate.Description ?? "").ToList();
            // 进程内选卡只能给 Avalonia 一个候选下标: 先用选卡值(PCI slot, 或早期存下的显示名)在本机目录里定位,
            // 同名多卡再按目录里的次序对到第 N 个同名候选。
            var adapters = ListAdapters();
            var found = IndexOfSelection(adapters, preferred);
            var index = MatchAdapterIndex(descriptions, found >= 0 ? adapters[found].Name : preferred, SameNameOrdinal(adapters, found));
            _activeAdapter = descriptions[index];
            return index;
        };
        builder.With(options);
    }

    [SupportedOSPlatform("linux")]
    private static void ApplyLinux(AppBuilder builder, string backend, string preferred)
    {
        ApplyPrimeSelection(preferred);
        IReadOnlyList<X11RenderingMode> modes = backend switch
        {
            SoftwareBackend => [X11RenderingMode.Software],
            EglBackend => [X11RenderingMode.Egl, X11RenderingMode.Software],
            VulkanBackend => [X11RenderingMode.Vulkan, X11RenderingMode.Software],
            _ => [X11RenderingMode.Glx, X11RenderingMode.Software],
        };
        builder.With(new X11PlatformOptions { RenderingMode = modes });
        _activeAdapter = preferred.Equals(AutoAdapter, StringComparison.OrdinalIgnoreCase) ? null : preferred;
    }

    /**
     * Linux 没有"进程内选卡"的公开 API: 用 PRIME 选择器表达偏好, 供 Mesa/NVIDIA 在创建 GL/Vulkan 设备时使用。
     * 这些变量必须在图形栈初始化前设置, 所以放在 AppBuilder 之前(同一进程内首次加载 GL 之前)执行。
     */
    [SupportedOSPlatform("linux")]
    private static void ApplyPrimeSelection(string preferred)
    {
        Environment.SetEnvironmentVariable("DRI_PRIME", null);
        Environment.SetEnvironmentVariable("__NV_PRIME_RENDER_OFFLOAD", null);
        Environment.SetEnvironmentVariable("__GLX_VENDOR_LIBRARY_NAME", null);
        if (preferred.Equals(AutoAdapter, StringComparison.OrdinalIgnoreCase))
            return;
        // 选卡值可能是 PCI slot(同名多卡唯一可区分的形式), 也可能仍是显示名
        var adapter = FindAdapter(preferred);
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

    /** 适配器 Id 是否为 PCI slot 形态(Windows 与 Linux 同形): 域:总线:设备.功能。 */
    public static bool LooksLikePciSlot(string id)
        => id.Count(character => character == ':') == 2 && id.Contains('.');

    /** /sys/class/drm 下的卡节点名: cardN(排除 cardN-输出名 与 renderD*)。 */
    private static bool IsCardNodeName(string name)
        => name.StartsWith("card", StringComparison.Ordinal) && name.Length > 4 && name[4..].All(char.IsAsciiDigit);

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

    private static string Normalize(string backend)
    {
        var normalized = backend.Trim().ToLowerInvariant();
        return normalized.Length > 0 ? normalized : AutoBackend;
    }
}
