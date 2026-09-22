using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Gui.ViewModels;

public enum SettingsSection
{
    Appearance,
    Model,
    Safety,
    Graphics,
    Storage,
    Plugins,
    Shortcuts,
    About,
}

/** 设置页状态: 八个分页共用一份草稿, 写入分别落在 GUI 插件参数段与 harness 设置上。 */
public sealed partial class SettingsViewModel : ObservableObject
{
    private const string CompactionPackage = "@deepseek-ai/dsh-compaction";
    private const int MaxStatusChars = 160;

    private readonly Context _ctx;
    private readonly HarnessHome _home;
    private readonly Func<AgentLoopAgent> _agent;
    private readonly CommandBridge _bridge;
    private readonly GuiSettings _gui;
    private readonly SettingsFacade _facade;

    public SettingsViewModel(
        Context ctx,
        HarnessHome home,
        Func<AgentLoopAgent> agent,
        CommandBridge bridge,
        GuiSettings gui,
        SettingsFacade facade)
    {
        _ctx = ctx;
        _home = home;
        _agent = agent;
        _bridge = bridge;
        _gui = gui;
        _facade = facade;
        foreach (var section in Sections)
            section.SelectCommand = new RelayCommand<SettingsNavItemViewModel>(SelectSection);
        Sections[0].IsSelected = true;
        Reload();
    }

    /** 主题/字号等外观项变化后由视图重新应用 App 资源。 */
    public event Action? Applied;

    public ObservableCollection<SettingsNavItemViewModel> Sections { get; } =
    [
        new(SettingsSection.Appearance, "外观"),
        new(SettingsSection.Model, "模型提供者"),
        new(SettingsSection.Safety, "安全策略"),
        new(SettingsSection.Graphics, "图形与加速"),
        new(SettingsSection.Storage, "会话与存储"),
        new(SettingsSection.Plugins, "插件与 MCP"),
        new(SettingsSection.Shortcuts, "快捷键"),
        new(SettingsSection.About, "关于"),
    ];

    public ObservableCollection<string> Themes { get; } = [GuiSettings.ThemeDark, GuiSettings.ThemeLight, GuiSettings.ThemeSystem];

    public ObservableCollection<string> ThemeLabels { get; } = ["深色", "浅色", "跟随系统"];

    /** 显卡候选: 自动 + 本机枚举到的每张卡(Windows 读显示类驱动注册表, Linux 读 /sys/class/drm)。 */
    public ObservableCollection<GpuAdapterOption> GpuAdapters { get; } = [];

    public ObservableCollection<string> GpuBackends { get; } = [GpuPreference.AutoBackend, "opengl", "vulkan", "software"];

    public ObservableCollection<string> CloseActions { get; } = [GuiSettings.CloseTray, GuiSettings.CloseQuit, GuiSettings.CloseAsk];

    public ObservableCollection<string> CloseActionLabels { get; } = ["最小化到托盘", "直接退出", "每次询问"];

    public ObservableCollection<string> Models { get; } = [];

    public ObservableCollection<string> ReasoningEfforts { get; } = [];

    public ObservableCollection<ProviderRowViewModel> Providers { get; } = [];

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    public ObservableCollection<McpRowViewModel> McpServers { get; } = [];

    public ObservableCollection<ShortcutRowViewModel> Shortcuts { get; } =
    [
        new("Enter", "发送消息"),
        new("Shift+Enter", "换行"),
        new("/", "命令候选（高级通道）"),
        new("@", "引用文件/文件夹/会话"),
        new("Esc", "返回对话 / 取消运行中的任务"),
        new("Ctrl+N", "新会话"),
        new("Ctrl+B", "折叠或展开左栏"),
        new("Ctrl+1 / Ctrl+2", "对话 / 轨迹"),
        new("Ctrl+,", "设置"),
        new("Ctrl+W", "最小化到托盘（按关闭行为）"),
        new("Ctrl+Q", "退出程序"),
        new("Tab / ↑ / ↓ / Enter", "候选浮层内导航与选择"),
    ];

    public SettingsSection Section { get; private set; }

    [ObservableProperty]
    private bool _isAppearanceSection = true;

    [ObservableProperty]
    private bool _isModelSection;

    [ObservableProperty]
    private bool _isSafetySection;

    [ObservableProperty]
    private bool _isGraphicsSection;

    [ObservableProperty]
    private bool _isStorageSection;

    [ObservableProperty]
    private bool _isPluginsSection;

    [ObservableProperty]
    private bool _isShortcutsSection;

    [ObservableProperty]
    private bool _isAboutSection;

    [ObservableProperty]
    private string _theme = GuiSettings.ThemeDark;

    [ObservableProperty]
    private double _fontSize = 13.5;

    [ObservableProperty]
    private string _fontSizeText = "13.5";

    [ObservableProperty]
    private string _workspaceView = GuiSettings.ViewSolution;

    [ObservableProperty]
    private string _providerName = "";

    [ObservableProperty]
    private string _providerType = "openai-compatible";

    [ObservableProperty]
    private string _providerBaseUrl = "";

    [ObservableProperty]
    private string _providerApiKey = "";

    [ObservableProperty]
    private string _providerModelIds = "";

    [ObservableProperty]
    private ProviderRowViewModel? _selectedProvider;

    [ObservableProperty]
    private bool _autoApprove;

    [ObservableProperty]
    private string _blacklistText = "";

    [ObservableProperty]
    private bool _gpuEnabled = true;

    [ObservableProperty]
    private string _selectedGpuBackend = "auto";

    [ObservableProperty]
    private string _gpuAdapter = GpuPreference.AutoAdapter;

    [ObservableProperty]
    private GpuAdapterOption? _selectedGpuAdapterOption;

    [ObservableProperty]
    private string _gpuCurrent = "";

    [ObservableProperty]
    private string _gpuHint = "";

    [ObservableProperty]
    private string _selectedCloseAction = GuiSettings.CloseTray;

    [ObservableProperty]
    private bool _traySupported = true;

    [ObservableProperty]
    private string _trayHint = "";

    [ObservableProperty]
    private string _sessionsPath = "";

    [ObservableProperty]
    private bool _compactionAuto = true;

    [ObservableProperty]
    private double _compactionThreshold = 80;

    [ObservableProperty]
    private string _pluginInput = "";

    [ObservableProperty]
    private string _versionText = "";

    [ObservableProperty]
    private string _commitText = "";

    [ObservableProperty]
    private string _configPath = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isWorking;

    public void Reload()
    {
        var snapshot = _gui.Load();
        Theme = snapshot.Theme;
        FontSize = snapshot.FontSize;
        FontSizeText = snapshot.FontSize.ToString("0.0");
        WorkspaceView = snapshot.WorkspaceView;
        GpuEnabled = snapshot.GpuEnabled;
        SelectedGpuBackend = snapshot.GpuBackend;
        GpuAdapter = snapshot.GpuAdapter;
        LoadGpuAdapters(snapshot.GpuAdapter);
        SelectedCloseAction = snapshot.CloseAction;
        TraySupported = ClosePolicy.TraySupported;
        TrayHint = TraySupported
            ? "tray = 最小化到托盘，quit = 直接退出，ask = 每次询问（当前桌面已检测到托盘宿主）"
            : "tray = 最小化到托盘，quit = 直接退出，ask = 每次询问；当前桌面没有托盘宿主，tray 会按 quit 处理（GNOME 可安装 AppIndicator 扩展后再试）";
        SessionsPath = Path.Combine(_home.Root, "sessions");
        ConfigPath = Path.Combine(_home.Root, "settings.yaml");
        LoadHarnessSettings();
        LoadVersion();
    }

    private void LoadGpuAdapters(string saved)
    {
        GpuAdapters.Clear();
        GpuAdapters.Add(new GpuAdapterOption(GpuPreference.AutoAdapter, "自动（系统默认）"));
        foreach (var adapter in GpuPreference.ListAdapters())
            GpuAdapters.Add(new GpuAdapterOption(adapter.Name, $"{adapter.Name}（{adapter.Detail}）"));
        if (saved.Length > 0 && !GpuAdapters.Any(option => option.Value == saved))
            GpuAdapters.Add(new GpuAdapterOption(saved, $"{saved}（当前不可用，仍按它启动）"));
        SelectedGpuAdapterOption = GpuAdapters.FirstOrDefault(option => option.Value == saved) ?? GpuAdapters[0];
        GpuCurrent = $"{GpuPreference.DescribeCurrent()} · 本机识别 {GpuAdapters.Count - 1} 张卡";
        GpuHint = OperatingSystem.IsLinux()
            ? "Linux 用 PRIME 选择器表达偏好（DRI_PRIME / __NV_PRIME_RENDER_OFFLOAD），部分驱动或容器里可能不生效；保存后重启生效。"
            : "选择的是交给渲染后端使用的显卡（例如 NVIDIA 独显 / AMD 集显）；保存后重启生效，后端不认这张卡时会回退到默认卡。";
    }

    public void ApplyTheme(string theme)
    {
        Theme = theme;
        _gui.Save(_gui.Load() with { Theme = theme });
        RefreshThemeFlags();
        Applied?.Invoke();
    }

    public void ApplyWorkspaceView(string view)
    {
        WorkspaceView = view;
        _gui.Save(_gui.Load() with { WorkspaceView = view });
        RefreshThemeFlags();
    }

    public bool IsDarkTheme => Theme == GuiSettings.ThemeDark;

    public bool IsLightTheme => Theme == GuiSettings.ThemeLight;

    public bool IsSystemTheme => Theme == GuiSettings.ThemeSystem;

    public bool IsSolutionDefault => WorkspaceView != GuiSettings.ViewFilesystem;

    public bool IsFileSystemDefault => WorkspaceView == GuiSettings.ViewFilesystem;

    [RelayCommand]
    private void SelectTheme(string theme) => ApplyTheme(theme switch
    {
        GuiSettings.ThemeLight => GuiSettings.ThemeLight,
        GuiSettings.ThemeSystem => GuiSettings.ThemeSystem,
        _ => GuiSettings.ThemeDark,
    });

    [RelayCommand]
    private void SelectWorkspaceView(string view)
        => ApplyWorkspaceView(view == GuiSettings.ViewFilesystem ? GuiSettings.ViewFilesystem : GuiSettings.ViewSolution);

    private void RefreshThemeFlags()
    {
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsSolutionDefault));
        OnPropertyChanged(nameof(IsFileSystemDefault));
    }

    [RelayCommand]
    private void PersistAppearance()
    {
        _gui.Save(_gui.Load() with { FontSize = Math.Clamp(FontSize, GuiSettings.MinFontSize, GuiSettings.MaxFontSize) });
        Status = "外观已保存";
        Applied?.Invoke();
    }

    [RelayCommand]
    private void ResetFontSize()
    {
        FontSize = 13.5;
        FontSizeText = "13.5";
        PersistAppearance();
    }

    [RelayCommand]
    private async Task SaveProviderAsync()
    {
        if (ProviderName.Trim().Length == 0 || ProviderBaseUrl.Trim().Length == 0 || ProviderApiKey.Trim().Length == 0)
        {
            Status = "需要填写 name、baseUrl 与 apiKey";
            return;
        }
        IsWorking = true;
        try
        {
            if (Providers.Any(row => string.Equals(row.Name, ProviderName.Trim(), StringComparison.Ordinal)))
                Status = await RunAsync($"/provider remove {ProviderName.Trim()}");
            var modelIds = ProviderModelIds.Trim();
            var suffix = modelIds.Length > 0 ? $" --model-ids {modelIds}" : "";
            Status = await RunAsync($"/provider add {ProviderName.Trim()} --base-url {ProviderBaseUrl.Trim()} --api-key {ProviderApiKey.Trim()} --type {ProviderType}{suffix}");
            LoadHarnessSettings();
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task RemoveProviderAsync()
    {
        if (SelectedProvider is not { } row)
            return;
        IsWorking = true;
        try
        {
            Status = await RunAsync($"/provider remove {row.Name}");
            LoadHarnessSettings();
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var provider = SelectedProvider?.Name ?? ProviderName.Trim();
        if (provider.Length == 0)
        {
            Status = "先选择一个 Provider";
            return;
        }
        IsWorking = true;
        try
        {
            var llm = _ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false);
            var models = llm?.ListModels(provider) ?? [];
            Status = models.Count > 0
                ? $"Provider {provider} 可用: {models.Count} 个模型（{string.Join(", ", models.Take(3).Select(model => model.Id))}…）"
                : $"Provider {provider} 未返回模型，检查 baseUrl/apiKey 与适配器是否加载";
        }
        catch (Exception error)
        {
            Status = $"连接失败: {error.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private void SaveSafety()
    {
        var rules = BlacklistText
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _facade.UpdateSafety(AutoApprove, rules);
        Status = $"安全策略已保存（{rules.Length} 条黑名单）";
    }

    [RelayCommand]
    private void SaveGraphics()
    {
        GpuAdapter = SelectedGpuAdapterOption?.Value ?? GpuPreference.AutoAdapter;
        _gui.Save(_gui.Load() with
        {
            GpuEnabled = GpuEnabled,
            GpuBackend = SelectedGpuBackend,
            GpuAdapter = GpuAdapter,
            CloseAction = SelectedCloseAction,
        });
        LoadGpuAdapters(GpuAdapter);
        var adapter = GpuAdapter == GpuPreference.AutoAdapter ? "自动" : GpuAdapter;
        var close = SelectedCloseAction switch
        {
            GuiSettings.CloseQuit => "退出",
            GuiSettings.CloseAsk => "询问",
            _ => TraySupported ? "最小化到托盘" : "最小化到托盘（无托盘宿主，实际按退出处理）",
        };
        Status = $"图形与关闭行为已保存：显卡 {adapter} · 后端 {GpuPreference.BackendLabel(GpuEnabled ? SelectedGpuBackend : GpuPreference.SoftwareBackend)} · 关闭时 {close}（重启生效）";
    }

    [RelayCommand]
    private void SaveStorage()
    {
        var settings = HarnessSettings.Load(_home);
        settings.Compaction = new CompactionSettings { Auto = CompactionAuto, Prune = settings.Compaction?.Prune ?? true };
        settings.Save(_home);
        _facade.SavePluginParameters(CompactionPackage, new Dictionary<string, object?>
        {
            ["auto"] = CompactionAuto,
            ["thresholdRatio"] = Math.Clamp(CompactionThreshold / 100, 0.1, 0.95),
        });
        Status = $"会话与存储已保存（自动压缩 {(CompactionAuto ? "开" : "关")}，阈值 {CompactionThreshold:0}%）";
    }

    [RelayCommand]
    private async Task RefreshPluginsAsync()
    {
        LoadHarnessSettings();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task AddPluginAsync()
    {
        if (PluginInput.Trim().Length == 0 || Manager() is not { } manager)
            return;
        IsWorking = true;
        try
        {
            Status = await manager.AddAsync(PluginInput.Trim());
            PluginInput = "";
            await RefreshPluginsAsync();
        }
        catch (Exception error)
        {
            Status = $"插件安装失败: {error.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task PluginActionAsync(string action)
    {
        if (Manager() is not { } manager)
            return;
        var parts = action.Split(':', 2);
        if (parts.Length != 2)
            return;
        IsWorking = true;
        try
        {
            Status = parts[0] switch
            {
                "enable" => await manager.EnableAsync(parts[1]),
                "disable" => await manager.DisableAsync(parts[1]),
                "remove" => await manager.RemoveAsync(parts[1]),
                _ => "未知操作",
            };
            await RefreshPluginsAsync();
        }
        catch (Exception error)
        {
            Status = $"插件操作失败: {error.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task OpenSessionsFolderAsync() => Status = await DesktopIntegration.OpenPathAsync(SessionsPath);

    [RelayCommand]
    private async Task OpenConfigFolderAsync() => Status = await DesktopIntegration.OpenPathAsync(Path.GetDirectoryName(ConfigPath) ?? _home.Root);

    private void SelectSection(SettingsNavItemViewModel? section)
    {
        if (section is null)
            return;
        Section = section.Section;
        foreach (var item in Sections)
            item.IsSelected = ReferenceEquals(item, section);
        IsAppearanceSection = Section == SettingsSection.Appearance;
        IsModelSection = Section == SettingsSection.Model;
        IsSafetySection = Section == SettingsSection.Safety;
        IsGraphicsSection = Section == SettingsSection.Graphics;
        IsStorageSection = Section == SettingsSection.Storage;
        IsPluginsSection = Section == SettingsSection.Plugins;
        IsShortcutsSection = Section == SettingsSection.Shortcuts;
        IsAboutSection = Section == SettingsSection.About;
        if (Section == SettingsSection.Model)
            LoadHarnessSettings();
        if (Section == SettingsSection.Plugins)
            LoadPluginRows();
    }

    private async Task<string> RunAsync(string line)
    {
        var text = await _bridge.RunAsync(_agent(), line);
        return text.Length > MaxStatusChars ? text[..MaxStatusChars] + "…" : text;
    }

    private void LoadHarnessSettings()
    {
        var settings = HarnessSettings.Load(_home);
        Models.Clear();
        foreach (var (providerName, providerEntry) in settings.Providers.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            foreach (var modelId in providerEntry.Models.Keys.OrderBy(id => id, StringComparer.Ordinal))
                Models.Add($"{providerName}/{modelId}");
        }
        Providers.Clear();
        foreach (var (providerName, providerEntry) in settings.Providers.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            Providers.Add(new ProviderRowViewModel(providerName, providerEntry.Type ?? "openai-compatible", providerEntry.Options?.BaseUrl ?? "", providerEntry.Models.Count));
        SelectedProvider = Providers.FirstOrDefault();
        AutoApprove = settings.Safety?.AutoApprove ?? false;
        BlacklistText = string.Join(Environment.NewLine, settings.Safety?.Blacklist ?? []);
        CompactionAuto = settings.Compaction?.Auto ?? true;
        McpServers.Clear();
        foreach (var (name, server) in settings.McpServers.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var target = server.Url ?? (server.Command is { Count: > 0 } command ? string.Join(' ', command) : "-");
            McpServers.Add(new McpRowViewModel(name, server.Transport ?? "stdio", target, server.Enabled));
        }
        var compaction = settings.Plugins.GetValueOrDefault(CompactionPackage)?.Parameters;
        if (compaction?.GetValueOrDefault("thresholdRatio") is double ratio)
            CompactionThreshold = ratio * 100;
        LoadReasoningEfforts();
        LoadPluginRows();
    }

    private void LoadReasoningEfforts()
    {
        ReasoningEfforts.Clear();
        var agent = _agent();
        var llm = _ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false);
        var header = agent.Session.RequestHeader();
        var provider = header?.Config.Provider ?? agent.Options.Provider ?? "";
        var model = header?.Config.Model ?? agent.Options.Model ?? "";
        try
        {
            foreach (var effort in llm?.ResolveModelInfo(provider, model).Reasoning?.Efforts ?? [])
                ReasoningEfforts.Add(effort.Id.Value);
        }
        catch (Exception)
        {
            ReasoningEfforts.Clear();
        }
    }

    private void LoadPluginRows()
    {
        Plugins.Clear();
        if (Manager() is not { } manager)
            return;
        var settings = HarnessSettings.Load(_home);
        foreach (var package in manager.PackageNames.OrderBy(name => name, StringComparer.Ordinal))
        {
            var enabled = settings.Plugins.GetValueOrDefault(package)?.Enabled ?? true;
            Plugins.Add(new PluginRowViewModel(package, manager.Describe(package), enabled));
        }
    }

    private void LoadVersion()
    {
        var assembly = typeof(SettingsViewModel).Assembly;
        VersionText = assembly.GetName().Version?.ToString() ?? "0.0.0";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = informational.IndexOf('+');
        CommitText = plus >= 0 && plus + 1 < informational.Length ? informational[(plus + 1)..] : "unknown";
        Status = "";
    }

    private IPluginManager? Manager() => _ctx.Get<IPluginManager>("pluginManager", false);
}

public sealed partial class SettingsNavItemViewModel(SettingsSection section, string label) : ObservableObject
{
    public SettingsSection Section { get; } = section;

    public string Label { get; } = label;

    public RelayCommand<SettingsNavItemViewModel>? SelectCommand { get; set; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record ProviderRowViewModel(string Name, string Type, string BaseUrl, int ModelCount);

public sealed record McpRowViewModel(string Name, string Transport, string Target, bool Enabled);

public sealed record ShortcutRowViewModel(string Keys, string Description);

/** 显卡下拉的一项: Value 写进 settings.yaml 的 gpu.adapter, Label 给人看。 */
public sealed record GpuAdapterOption(string Value, string Label);

public sealed class PluginRowViewModel(string package, string description, bool enabled) : ObservableObject
{
    public string Package { get; } = package;

    public string Description { get; } = description;

    public bool Enabled { get; } = enabled;

    public string ActionLabel => Enabled ? "禁用" : "启用";

    public string WireAction => $"{(Enabled ? "disable" : "enable")}:{Package}";
}
