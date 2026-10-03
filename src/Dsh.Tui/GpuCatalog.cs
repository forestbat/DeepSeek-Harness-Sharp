using System.Globalization;
using System.Runtime.Versioning;
using Dsh.Boot;
using Microsoft.Win32;

namespace Dsh.Tui;

public sealed record GpuAdapterInfo(string Id, string Name, string Vendor, string Detail, bool? IsUma);

/**
 * 显卡目录: 枚举本机显卡(Windows 显示类驱动注册表 / Linux /sys/class/drm)、按显存架构分辨核显(UMA)/独显、保存用户选卡。
 * 选卡标识用 PCI slot(同名多卡唯一可区分): Linux 取 sysfs 的 PCI_SLOT_NAME, Windows 由显示类子键回查 Enum\PCI 的设备实例,
 * 拿不到 slot 时退回设备实例路径(设备实例 ID), 再退回注册表键名与显示名。
 * 选卡持久化在 settings.yaml 的 plugins."@deepseek-ai/dsh-gui".gpu.adapter, GUI 设置页与 TUI /gpu 命令共用同一键, 重启进程后生效。
 */
public static class GpuCatalog
{
    public const string AutoAdapter = "auto";

    private const string GpuSettingsPackage = "@deepseek-ai/dsh-gui";
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
        // 枚举要读注册表 + 扫 Enum\PCI + 探 UMA(单次可达数秒), 进程内适配器不会变, 缓存一次。
        lock (AdapterCacheLock)
            return _adapterCache ??= LoadAdapters();
    }

    private static IReadOnlyList<GpuAdapterInfo> LoadAdapters()
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

    private static readonly object AdapterCacheLock = new();
    private static IReadOnlyList<GpuAdapterInfo>? _adapterCache;

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

    /** 选卡候选与 /gpu 展示共用同一组可读文本: 第 0 项是 auto, 其后每张卡 "名字 (kind, detail)"。 */
    public static IReadOnlyList<string> SelectionLabels()
    {
        var labels = new List<string> { $"{AutoAdapter} (system default)" };
        foreach (var adapter in ListAdapters())
        {
            var kind = IsDiscrete(adapter) ? "discrete" : "integrated";
            labels.Add($"{adapter.Name} ({kind}, {adapter.Detail})");
        }
        return labels;
    }

    public const string WindowsAppHostName = "DeepSeek-Harness-Sharp";
    public const string WindowsGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /** 进程级 GPU 偏好的数据值: 独显=2(高性能)/核显=1(节能)。 */
    public static string WindowsPreferenceData(bool discrete) => $"GpuPreference={(discrete ? 2 : 1)};";

    /**
     * Windows 只支持进程级 GPU 偏好(WGL 按它落卡): 写 HKCU 下 UserGpuPreferences, 值名=进程映像全路径。
     * auto 删除该项; 仅对产品 apphost 生效, 拒绝对 dotnet.exe 写入(那会波及所有 .NET 应用)。
     */
    public static string ApplyWindowsPreference(string selection)
    {
        if (!OperatingSystem.IsWindows())
            return "";
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            return "无法确定当前进程映像路径, 未写入 Windows 选卡偏好";
        var name = Path.GetFileNameWithoutExtension(processPath);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return "当前以 dotnet.exe 运行, 拒绝写入进程级 GPU 偏好(会影响所有 .NET 应用); 请用 apphost exe 启动后再选卡";
        if (!name.Equals(WindowsAppHostName, StringComparison.OrdinalIgnoreCase))
            return "";
        using var key = Registry.CurrentUser.CreateSubKey(WindowsGpuPreferencesKey, writable: true);
        if (key is null)
            return "无法打开 Windows GPU 偏好注册表键";
        if (selection == AutoAdapter)
        {
            key.DeleteValue(processPath, throwOnMissingValue: false);
            return "已清除 Windows 进程级 GPU 偏好(auto)";
        }
        var adapter = ListAdapters().FirstOrDefault(candidate => SelectionIdOf(candidate) == selection);
        var data = WindowsPreferenceData(adapter is not null && IsDiscrete(adapter));
        key.SetValue(processPath, data, RegistryValueKind.String);
        return $"已写 Windows 进程级 GPU 偏好 {data} — 重启生效";
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

    /**
     * 独立窗口形态是否垂直同步(settings.yaml 的 plugins."@deepseek-ai/dsh-gui".gpu.vsync, 缺省 true)。
     * 不写死: 关掉会按显卡最快速度空转(实测上万 fps), 开着则跟显示器刷新同步; 具体取值由用户配置。
     */
    public static bool LoadVsync(HarnessHome home)
    {
        if (HarnessSettings.Load(home).Plugins.GetValueOrDefault(GpuSettingsPackage)?.Parameters is not { } parameters
            || parameters.GetValueOrDefault("gpu") is not IReadOnlyDictionary<string, object?> gpu)
            return true;
        return gpu.GetValueOrDefault("vsync") switch
        {
            bool value => value,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => true,
        };
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
            var identity = WindowsDeviceIdentity(name);
            var vendor = VendorOf(deviceId);
            var id = identity.PciSlot ?? identity.DeviceInstancePath ?? name;
            adapters.Add(new GpuAdapterInfo(id, driverDesc, vendor, $"{vendor} · {identity.PciSlot ?? ShortDeviceId(deviceId)}", ProbeWindowsUma(deviceId)));
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
     * 适配器 Id 是否为 PCI slot 形态(Windows 与 Linux 同形)。同名多卡(如集群里一批同型号)只有 slot 能区分,
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
