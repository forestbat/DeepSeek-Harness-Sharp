using System.Runtime.Versioning;
using Avalonia;
using Microsoft.Win32;

namespace Dsh.Gui.Services;

public sealed record GpuAdapterInfo(string Id, string Name, string Vendor, string Detail);

/**
 * 图形设置: 主选项是「用哪张显卡」, 渲染后端只是高级项。
 * Windows: 显卡列表读显示类驱动注册表(DriverDesc + MatchingDeviceId), 选择通过 Avalonia 的显卡选择回调按名字匹配;
 * Linux: 显卡列表读 /sys/class/drm, 选择通过 PRIME 环境变量(DRI_PRIME / __NV_PRIME_RENDER_OFFLOAD)在启动时生效。
 */
public static class GpuPreference
{
    public const string AutoAdapter = "auto";
    public const string SoftwareBackend = "software";
    public const string AutoBackend = "auto";
    public const string OpenGlBackend = "opengl";
    public const string VulkanBackend = "vulkan";

    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string DriverDescValue = "DriverDesc";
    private const string MatchingDeviceIdValue = "MatchingDeviceId";
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
            var vendor = VendorOf(deviceId);
            adapters.Add(new GpuAdapterInfo(name, driverDesc, vendor, $"{vendor} · {ShortDeviceId(deviceId)}"));
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

    /** Avalonia 给出的候选里按名字挑用户选中的那张; 匹配不到就用默认(0)。 */
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
            var index = MatchAdapterIndex(descriptions, preferred);
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
        // 选卡值可能是 PCI slot(同名多卡唯一可区分的形式, TUI /gpu 在 Linux 就存它), 也可能仍是显示名
        var adapters = ListLinuxAdapters();
        var adapter = LooksLikePciSlot(preferred)
            ? adapters.FirstOrDefault(candidate => string.Equals(candidate.Id, preferred, StringComparison.OrdinalIgnoreCase))
            : adapters.FirstOrDefault(candidate => preferred.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase))
                ?? adapters.FirstOrDefault(candidate => preferred.Contains(candidate.Vendor, StringComparison.OrdinalIgnoreCase));
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

    private static bool LooksLikePciSlot(string id)
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
