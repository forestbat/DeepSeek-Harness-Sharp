using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Workspace;
using Dsh.Gui.Services;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

public enum SettingsSection
{
    Appearance,
    Model,
    Account,
    Safety,
    Graphics,
    Storage,
    Plugins,
    Shortcuts,
    Network,
    Ide,
    About,
}

/** 设置页状态: 各分页共用一份草稿, 写入分别落在 GUI 插件参数段与 harness 设置上。 */
public sealed partial class SettingsViewModel : ObservableObject
{
    private const string CompactionPackage = "@deepseek-ai/dsh-compaction";
    private const int MaxStatusChars = 160;

    private readonly Context _ctx;
    private readonly HarnessHome _home;
    private readonly Func<string, Task<string>> _run;
    private readonly GuiSettings _gui;
    private readonly SettingsFacade _facade;
    private readonly Func<Task<IWorkspacePlugins>> _plugins;
    private readonly IWorkspaceProxyProbe _proxyProbe;
    private string _draftProxyUrl = "";
    private string _draftProxyProbeUrl = "";
    private bool _loadingNetwork;
    private long _networkGeneration;
    private bool _networkShutdown;

    public SettingsViewModel(
        Context ctx,
        HarnessHome home,
        Func<string, Task<string>> run,
        GuiSettings gui,
        SettingsFacade facade,
        Func<Task<IWorkspacePlugins>> plugins)
    {
        _ctx = ctx;
        _home = home;
        _run = run;
        _gui = gui;
        _facade = facade;
        _plugins = plugins;
        _proxyProbe = facade.CreateProxyProbe();
        LocalProxyProbe.PropertyChanged += OnLocalLayerProbeStateChanged;
        BorrowedOutboundProbe.PropertyChanged += OnLocalLayerProbeStateChanged;
        Account = new AccountPanelViewModel(_ctx.Get<IAccountService>(IAccountService.ServiceName, false));
        Ide = new EditorSettingsViewModel(facade);
        foreach (var section in Sections)
            section.SelectCommand = new RelayCommand<SettingsNavItemViewModel>(SelectSection);
        Sections[0].IsSelected = true;
        Reload();
        LoadNetwork();
    }

    /** 主题/字号等外观项变化后由视图重新应用 App 资源。 */
    public event Action? Applied;

    /** 拖动字号滑块时的实时预览: 不写盘, 由 MainWindow 用内存值重应用外观。 */
    public event Action? Preview;

    partial void OnFontSizeChanged(double value)
    {
        FontSizeText = value.ToString("0.0");
        Preview?.Invoke();
    }

    public ObservableCollection<SettingsNavItemViewModel> Sections { get; } =
    [
        new(SettingsSection.Appearance, "外观"),
        new(SettingsSection.Model, "模型提供者"),
        new(SettingsSection.Account, "账号"),
        new(SettingsSection.Safety, "安全策略"),
        new(SettingsSection.Graphics, "图形与加速"),
        new(SettingsSection.Storage, "会话与存储"),
        new(SettingsSection.Plugins, "插件与 MCP"),
        new(SettingsSection.Shortcuts, "快捷键"),
        new(SettingsSection.Network, "网络"),
        new(SettingsSection.Ide, "IDE 与编辑器"),
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

    public AccountPanelViewModel Account { get; }
    public EditorSettingsViewModel Ide { get; }

    [ObservableProperty]
    private bool _isIdeSection;

    [ObservableProperty]
    private bool _isAppearanceSection = true;

    [ObservableProperty]
    private bool _isModelSection;

    [ObservableProperty]
    private bool _isAccountSection;

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
    private bool _isNetworkSection;

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

    /** 选择模型提供者: 前 7 项是协议族/自定义(只填 type), 其后是 models.dev 目录 provider(带出 name/baseUrl/type/models)。 */
    [ObservableProperty]
    private IReadOnlyList<ProviderChoiceViewModel> _providerChoices = [];

    [ObservableProperty]
    private ProviderChoiceViewModel? _selectedProviderChoice;

    [ObservableProperty]
    private string _providerType = ProviderTypes.OpenAiCompatible;

    /** `type` 的可选值(协议族 + custom 别名), 供下拉补全。 */
    public IReadOnlyList<string> ProviderTypeChoices { get; } = ProviderTypes.All;

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

    /** 模型切换后只需刷新推理强度候选; 完整 Reload 会读盘并重查 GPU/版本, 在点击链路里太重。 */
    public void RefreshReasoningEfforts() => LoadReasoningEfforts();

    public void Reload()
    {
        var snapshot = _gui.Load();
        Theme = snapshot.Theme;
        FontSize = snapshot.FontSize;
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
        var adapters = GpuPreference.ListAdapters();
        // 落盘用 PCI slot(同名多卡唯一可区分); 早期版本存的是显示名, 按目录定位后仍选中同一张卡。
        foreach (var adapter in adapters)
            GpuAdapters.Add(new GpuAdapterOption(GpuPreference.SelectionIdOf(adapter), $"{adapter.Name}（{adapter.Detail}）"));
        var matched = GpuPreference.IndexOfSelection(adapters, saved);
        if (matched >= 0)
            SelectedGpuAdapterOption = GpuAdapters[matched + 1];
        else
        {
            if (saved.Length > 0)
                GpuAdapters.Add(new GpuAdapterOption(saved, $"{saved}（当前不可用，仍按它启动）"));
            SelectedGpuAdapterOption = GpuAdapters[0];
        }
        GpuCurrent = $"{GpuPreference.DescribeCurrent()} · 本机识别 {adapters.Count} 张卡";
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
        PersistAppearance();
    }

    /**
     * 选中协议族/自定义: 只填 type(视为自定义端点, name/baseUrl 仍由用户填)。
     * 选中目录 provider: 带出 name/baseUrl/type/models, 用户只需再填 apiKey。
     */
    partial void OnSelectedProviderChoiceChanged(ProviderChoiceViewModel? value)
    {
        if (value is null)
            return;
        if (value.IsProtocolFamily)
        {
            ProviderType = value.Type ?? ProviderTypes.OpenAiCompatible;
            Status = $"已选协议族 {ProviderType}（自定义端点，请自行填写 name/baseUrl）";
            return;
        }

        var entry = ProviderCatalog.LoadCached(_home).Providers
            .FirstOrDefault(candidate => string.Equals(candidate.Id, value.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return;
        ProviderName = entry.Id;
        ProviderBaseUrl = entry.BaseUrl ?? "";
        ProviderType = entry.Type ?? ProviderTypes.OpenAiCompatible;
        ProviderModelIds = ProviderCatalog.AllModelsMarker;
        Status = entry.BaseUrl is null
            ? $"已按目录填入 {entry.Id}（该 provider 没有公开固定 baseUrl，请自行填写）"
            : $"已按目录填入 {entry.Id}，补 apiKey 即可保存";
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
            var type = ProviderTypes.Canonical(ProviderType);
            Status = await RunAsync($"/provider add {ProviderName.Trim()} --base-url {ProviderBaseUrl.Trim()} --api-key {ProviderApiKey.Trim()} --type {type}{suffix}");
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
            var runtime = _ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false);
            var models = runtime?.ListModels(provider) ?? [];
            Status = models.Count > 0
                ? $"Provider {provider} 可用: {models.Count} 个模型（{string.Join(", ", models.Take(3))}…）"
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
        LoadHarnessSettings(loadMetadata: false);
        await LoadReasoningEffortsAsync();
        await LoadPluginRowsAsync();
    }

    [RelayCommand]
    private async Task AddPluginAsync()
    {
        if (PluginInput.Trim().Length == 0)
            return;
        IsWorking = true;
        try
        {
            Status = await RunAsync($"/plugins add {PluginInput.Trim()}");
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

    /** Plugin library picker, wired by MainWindow to the platform storage provider. */
    public Func<Task<string?>>? PluginFilePicker { get; set; }

    [RelayCommand]
    private async Task BrowsePluginPathAsync()
    {
        if (PluginFilePicker is not null && await PluginFilePicker() is { Length: > 0 } path)
            PluginInput = path;
    }

    /** Market permission toggles: the same settings mutation as /plugins ban_write|enable_write. */
    private void SetPluginPermission(string package, PluginPermission permission, bool allowed)
        => _ = SetPluginPermissionAsync(package, permission, allowed);

    private async Task SetPluginPermissionAsync(string package, PluginPermission permission, bool allowed)
    {
        try
        {
            Status = await (await _plugins()).SetPluginPermissionAsync(package, permission.ToString(), allowed);
        }
        catch (Exception error)
        {
            Status = $"插件权限写入失败: {error.Message}";
        }
    }

    [RelayCommand]
    private async Task PluginActionAsync(string action)
    {
        var parts = action.Split(':', 2);
        if (parts.Length != 2)
            return;
        IsWorking = true;
        try
        {
            Status = parts[0] is "enable" or "disable" or "remove"
                ? await RunAsync($"/plugins {parts[0]} {parts[1]}")
                : "未知操作";
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

    public NetworkProbeViewModel LocalProxyProbe { get; } = new(ProxyProbeLayer.LocalOutbound);
    public NetworkProbeViewModel BorrowedOutboundProbe { get; } = new(ProxyProbeLayer.LocalOutbound);
    public ObservableCollection<string> BorrowedOutboundMachines { get; } = [];

    [ObservableProperty]
    private string _outboundProxyUrl = "";

    [ObservableProperty]
    private bool _isEditingOutboundProxy;

    [ObservableProperty]
    private string _outboundProxyBypassText = "";

    [ObservableProperty]
    private string _outboundProxyProbeUrl = "";

    [ObservableProperty]
    private string? _selectedBorrowedOutboundMachine;

    [ObservableProperty]
    private string _savedBorrowedOutboundMachine = "";

    [ObservableProperty]
    private string _networkStatus = "";

    [ObservableProperty]
    private string _borrowedOutboundStatus = "";

    [ObservableProperty]
    private bool _isChangingBorrowedOutbound;

    public string OutboundProxyProbePlaceholder { get; } = $"留空 = {OutboundProxySettings.DefaultProbeUrl}";

    partial void OnOutboundProxyUrlChanged(string value)
    {
        if (_loadingNetwork)
            return;
        _draftProxyUrl = value;
        LocalProxyProbe.Invalidate();
    }

    partial void OnOutboundProxyBypassTextChanged(string value) => LocalProxyProbe.Invalidate();
    partial void OnOutboundProxyProbeUrlChanged(string value)
    {
        if (_loadingNetwork)
            return;
        _draftProxyProbeUrl = value;
        LocalProxyProbe.Invalidate();
    }
    partial void OnSelectedBorrowedOutboundMachineChanged(string? value) => BorrowedOutboundProbe.Invalidate();

    [RelayCommand]
    private void EditOutboundProxy()
    {
        _loadingNetwork = true;
        try
        {
            OutboundProxyUrl = _draftProxyUrl;
            OutboundProxyProbeUrl = _draftProxyProbeUrl;
            IsEditingOutboundProxy = true;
        }
        finally
        {
            _loadingNetwork = false;
        }
    }

    private void MaskOutboundProxy()
    {
        _loadingNetwork = true;
        try
        {
            OutboundProxyUrl = OutboundProxy.RedactUrlCredentials(_draftProxyUrl, _draftProxyUrl);
            OutboundProxyProbeUrl = OutboundProxy.RedactUrlCredentials(_draftProxyProbeUrl, _draftProxyProbeUrl);
            IsEditingOutboundProxy = false;
        }
        finally
        {
            _loadingNetwork = false;
        }
    }

    private OutboundProxySettings DraftOutboundProxy() => new()
    {
        Url = string.IsNullOrWhiteSpace(_draftProxyUrl) ? null : _draftProxyUrl.Trim(),
        Bypass = [.. OutboundProxyBypassText.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        ProbeUrl = string.IsNullOrWhiteSpace(_draftProxyProbeUrl) ? null : _draftProxyProbeUrl.Trim(),
    };

    private void LoadNetwork()
    {
        DeactivateNetwork();
        var network = _facade.Load().Network;
        var proxy = network?.OutboundProxy;
        _draftProxyUrl = proxy?.Url ?? "";
        _draftProxyProbeUrl = proxy?.ProbeUrl ?? "";
        MaskOutboundProxy();
        OutboundProxyBypassText = string.Join(Environment.NewLine, proxy?.Bypass ?? []);
        BorrowedOutboundMachines.Clear();
        foreach (var machine in _facade.BorrowedOutboundMachines())
            BorrowedOutboundMachines.Add(machine);
        var selected = network?.BorrowedOutboundMachine;
        if (selected is not null && !BorrowedOutboundMachines.Contains(selected))
            BorrowedOutboundMachines.Add(selected);
        SelectedBorrowedOutboundMachine = selected;
        SavedBorrowedOutboundMachine = selected ?? "未借用（使用本机基准出口）";
        NetworkStatus = "";
        BorrowedOutboundStatus = "";
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SaveNetworkAsync(CancellationToken cancellationToken)
    {
        if (_networkShutdown)
            return;
        var generation = _networkGeneration;
        var draft = DraftOutboundProxy();
        NetworkStatus = "正在保存本机基准出口并重载同 home 本机 Host…";
        try
        {
            await _facade.UpdateOutboundProxyAsync(draft, cancellationToken);
            if (generation != _networkGeneration)
                return;
            MaskOutboundProxy();
            NetworkStatus = "本机基准出口已保存；当前进程与已连接同 home 本机 Host 已重载。借用选择未变。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == _networkGeneration)
                NetworkStatus = "操作已取消；如已写盘，不会回滚。可重新打开网络页核对当前配置。";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (generation == _networkGeneration)
                NetworkStatus = _facade.RedactNetworkError(OutboundProxy.RedactUrlCredentials(
                    $"保存或重载失败：{error.Message}", draft.Url, draft.ProbeUrl));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunLocalLayer))]
    private Task CheckProxyAsync()
    {
        if (!CanRunLocalLayer)
            return Task.CompletedTask;
        var draft = DraftOutboundProxy();
        return LocalProxyProbe.RunAsync(MachineSpec.LocalName, _home.Root,
            (operationId, token) => _proxyProbe.ProbeLocalAsync(_home.Root, draft, operationId, token), draft.Url, draft.ProbeUrl);
    }

    [RelayCommand(CanExecute = nameof(CanRunLocalLayer), IncludeCancelCommand = true)]
    private Task SelectBorrowedOutboundAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SelectedBorrowedOutboundMachine))
        {
            BorrowedOutboundStatus = "请先选择已配置出口隧道的 SSH / iroh 机器；清除借用请使用独立清除按钮。";
            return Task.CompletedTask;
        }
        return ChangeBorrowedOutboundAsync(SelectedBorrowedOutboundMachine, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanRunLocalLayer), IncludeCancelCommand = true)]
    private Task ClearBorrowedOutboundAsync(CancellationToken cancellationToken)
        => ChangeBorrowedOutboundAsync(null, cancellationToken);

    private async Task ChangeBorrowedOutboundAsync(string? machine, CancellationToken cancellationToken)
    {
        if (!CanRunLocalLayer)
            return;
        IsChangingBorrowedOutbound = true;
        var generation = _networkGeneration;
        BorrowedOutboundProbe.Invalidate();
        BorrowedOutboundStatus = "正在验证、保存借用选择并重载同 home 本机 Host…";
        try
        {
            await _facade.SelectBorrowedOutboundAsync(machine, cancellationToken);
            if (generation != _networkGeneration)
                return;
            SavedBorrowedOutboundMachine = machine ?? "未借用（使用本机基准出口）";
            SelectedBorrowedOutboundMachine = machine;
            BorrowedOutboundStatus = "借用选择已保存并生效；本机基准 URL、SSH 代理及远端部署代理均未修改。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == _networkGeneration)
                BorrowedOutboundStatus = "操作已取消；已保存的选择不会回滚，请重新打开网络页核对。";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (generation == _networkGeneration)
                BorrowedOutboundStatus = _facade.RedactNetworkError($"借用选择或重载失败：{error.Message}");
        }
        finally
        {
            IsChangingBorrowedOutbound = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunLocalLayer))]
    private Task ValidateBorrowedOutboundAsync()
    {
        if (!CanRunLocalLayer)
            return Task.CompletedTask;
        var machine = SelectedBorrowedOutboundMachine;
        if (string.IsNullOrWhiteSpace(machine))
        {
            BorrowedOutboundStatus = "请先选择已配置出口隧道的 SSH 机器。独立验证不会保存选择。";
            return Task.CompletedTask;
        }
        return BorrowedOutboundProbe.RunAsync(machine, _home.Root,
            (operationId, token) => _facade.ProbeBorrowedOutboundAsync(machine, operationId, token));
    }

    private bool CanRunLocalLayer => !_networkShutdown && !IsChangingBorrowedOutbound
        && !LocalProxyProbe.IsRunning && !BorrowedOutboundProbe.IsRunning;

    partial void OnIsChangingBorrowedOutboundChanged(bool value) => NotifyLocalLayerCommands();

    private void OnLocalLayerProbeStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NetworkProbeViewModel.IsRunning))
            return;
        NotifyLocalLayerCommands();
    }

    private void NotifyLocalLayerCommands()
    {
        CheckProxyCommand.NotifyCanExecuteChanged();
        ValidateBorrowedOutboundCommand.NotifyCanExecuteChanged();
        SelectBorrowedOutboundCommand.NotifyCanExecuteChanged();
        ClearBorrowedOutboundCommand.NotifyCanExecuteChanged();
    }

    public void BeginNetworkShutdown()
    {
        _networkShutdown = true;
        NotifyLocalLayerCommands();
        DeactivateNetwork();
    }

    public void DeactivateNetwork()
    {
        _networkGeneration++;
        LocalProxyProbe.Invalidate();
        BorrowedOutboundProbe.Invalidate();
        SaveNetworkCommand.Cancel();
        SelectBorrowedOutboundCommand.Cancel();
        ClearBorrowedOutboundCommand.Cancel();
        MaskOutboundProxy();
    }

    public async Task WaitForNetworkOperationsAsync()
    {
        await Task.WhenAll(
            LocalProxyProbe.WaitForCompletionAsync(),
            BorrowedOutboundProbe.WaitForCompletionAsync(),
            SaveNetworkCommand.ExecutionTask ?? Task.CompletedTask,
            SelectBorrowedOutboundCommand.ExecutionTask ?? Task.CompletedTask,
            ClearBorrowedOutboundCommand.ExecutionTask ?? Task.CompletedTask);
    }

    private void SelectSection(SettingsNavItemViewModel? section)
    {
        if (section is null)
            return;
        if (Section != section.Section)
            DeactivateNetwork();
        Section = section.Section;
        foreach (var item in Sections)
            item.IsSelected = ReferenceEquals(item, section);
        IsAppearanceSection = Section == SettingsSection.Appearance;
        IsModelSection = Section == SettingsSection.Model;
        IsAccountSection = Section == SettingsSection.Account;
        IsSafetySection = Section == SettingsSection.Safety;
        IsGraphicsSection = Section == SettingsSection.Graphics;
        IsStorageSection = Section == SettingsSection.Storage;
        IsPluginsSection = Section == SettingsSection.Plugins;
        IsShortcutsSection = Section == SettingsSection.Shortcuts;
        IsNetworkSection = Section == SettingsSection.Network;
        IsAboutSection = Section == SettingsSection.About;
        IsIdeSection = Section == SettingsSection.Ide;
        if (Section == SettingsSection.Model)
            LoadHarnessSettings();
        if (Section == SettingsSection.Plugins)
            LoadPluginRows();
        if (Section == SettingsSection.Network)
            LoadNetwork();
        if (Section == SettingsSection.Account)
            _ = Account.RefreshAsync();
    }

    private async Task<string> RunAsync(string line)
    {
        var text = await _run(line);
        return text.Length > MaxStatusChars ? text[..MaxStatusChars] + "…" : text;
    }

    private void LoadHarnessSettings(bool loadMetadata = true)
    {
        var settings = HarnessSettings.Load(_home);
        Models.Clear();
        foreach (var (providerName, providerEntry) in settings.Providers.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            foreach (var modelId in ProviderCatalog.EffectiveModels(providerName, providerEntry.Options?.BaseUrl, providerEntry.Models.Keys)
                .OrderBy(id => id, StringComparer.Ordinal))
            {
                Models.Add($"{providerName}/{modelId}");
            }
        }
        Providers.Clear();
        foreach (var (providerName, providerEntry) in settings.Providers.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var effective = ProviderCatalog.EffectiveModels(providerName, providerEntry.Options?.BaseUrl, providerEntry.Models.Keys);
            Providers.Add(new ProviderRowViewModel(providerName, ProviderTypes.Canonical(providerEntry.Type), providerEntry.Options?.BaseUrl ?? "", effective.Count));
        }
        ProviderChoices = ProviderChoiceViewModel.Build(ProviderCatalog.LoadCached(_home));
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
        if (loadMetadata)
        {
            LoadReasoningEfforts();
            LoadPluginRows();
        }
    }

    private void LoadReasoningEfforts() => _ = LoadReasoningEffortsAsync();

    private Task LoadReasoningEffortsAsync()
    {
        var route = HarnessSettings.Load(_home).ResolveDefaultModel();
        if (route is null)
            return Task.CompletedTask;
        try
        {
            var runtime = _ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false);
            IReadOnlyList<string> efforts = runtime is null
                ? []
                : [.. runtime.ResolveModelInfo(route.Value.Provider, route.Value.Model).Reasoning?.Efforts.Select(effort => effort.Id.Value) ?? []];
            // 每次打开浮层都会刷新候选; 列表没变时不要 Clear/Add, 否则 ItemsControl 会重建全部按钮, 造成打开卡顿。
            if (ReasoningEfforts.SequenceEqual(efforts, StringComparer.Ordinal))
                return Task.CompletedTask;
            ReasoningEfforts.Clear();
            foreach (var effort in efforts)
                ReasoningEfforts.Add(effort);
        }
        catch (Exception error)
        {
            Status = $"推理选项读取失败: {error.Message}";
        }
        return Task.CompletedTask;
    }

    private void LoadPluginRows() => _ = LoadPluginRowsAsync();

    private async Task LoadPluginRowsAsync()
    {
        try
        {
            var plugins = await (await _plugins()).ListPluginsAsync();
            Plugins.Clear();
            foreach (var plugin in plugins.OrderBy(item => item.Package, StringComparer.Ordinal))
                Plugins.Add(new PluginRowViewModel(plugin.Package, plugin.Description, plugin.Enabled,
                    new PluginPermissions { Read = plugin.ReadAllowed, Write = plugin.WriteAllowed, Network = plugin.NetworkAllowed }, SetPluginPermission));
        }
        catch (Exception error)
        {
            Status = $"插件列表读取失败: {error.Message}";
        }
    }

    private void LoadVersion()
    {
        var assembly = typeof(SettingsViewModel).Assembly;
        VersionText = HostProtocol.ReleaseVersion;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = informational.IndexOf('+');
        CommitText = plus >= 0 && plus + 1 < informational.Length ? informational[(plus + 1)..] : "unknown";
        Status = "";
    }

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

/**
 * "选择模型提供者"下拉的一项。
 * 协议族条目只有 Type(选中只填 type); 目录条目带 ProviderId(选中按目录带出 name/baseUrl/type/models)。
 * Display 带段前缀: 目录 id 与类型名会重名(openai/anthropic/deepseek 两边都有), 不区分就会歧义。
 */
public sealed record ProviderChoiceViewModel(string Display, string? ProviderId, string? Type)
{
    public const string ProtocolPrefix = "协议族 · ";

    public const string CatalogPrefix = "目录 · ";

    public bool IsProtocolFamily => ProviderId is null;

    /** 前 7 项协议族/自定义, 其后按 id 排序的目录 provider。 */
    public static IReadOnlyList<ProviderChoiceViewModel> Build(ProviderCatalogSnapshot catalog)
        => [
            .. ProviderTypes.All.Select(type => new ProviderChoiceViewModel($"{ProtocolPrefix}{type}", null, type)),
            .. catalog.Providers
                .Where(entry => entry.Type is not null)
                .OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new ProviderChoiceViewModel($"{CatalogPrefix}{entry.Id}", entry.Id, entry.Type)),
        ];

    /** AutoCompleteBox 按 ToString() 过滤, 因此筛选词匹配的是带前缀的显示名。 */
    public override string ToString() => Display;
}

public sealed record McpRowViewModel(string Name, string Transport, string Target, bool Enabled);

public sealed record ShortcutRowViewModel(string Keys, string Description);

/** 显卡下拉的一项: Value 写进 settings.yaml 的 gpu.adapter, Label 给人看。 */
public sealed record GpuAdapterOption(string Value, string Label);

/** One plugin row; permission toggles write back through setPermission as soon as they change. */
public sealed partial class PluginRowViewModel(
    string package,
    string description,
    bool enabled,
    PluginPermissions permissions,
    Action<string, PluginPermission, bool> setPermission) : ObservableObject
{
    public string Package { get; } = package;

    public string Description { get; } = description;

    public bool Enabled { get; } = enabled;

    public string ActionLabel => Enabled ? "禁用" : "启用";

    public string WireAction => $"{(Enabled ? "disable" : "enable")}:{Package}";

    public string RemoveAction => $"remove:{Package}";

    [ObservableProperty]
    private bool _canRead = permissions.Read;

    [ObservableProperty]
    private bool _canWrite = permissions.Write;

    [ObservableProperty]
    private bool _canNetwork = permissions.Network;

    partial void OnCanReadChanged(bool value) => setPermission(Package, PluginPermission.Read, value);

    partial void OnCanWriteChanged(bool value) => setPermission(Package, PluginPermission.Write, value);

    partial void OnCanNetworkChanged(bool value) => setPermission(Package, PluginPermission.Network, value);
}
