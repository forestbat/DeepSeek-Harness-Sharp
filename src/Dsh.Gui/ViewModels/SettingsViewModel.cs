using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Account;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;

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
    Remote,
    About,
}

/** 设置页状态: 九个分页共用一份草稿, 写入分别落在 GUI 插件参数段与 harness 设置上。 */
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
        Account = new AccountPanelViewModel(_ctx.Get<IAccountService>(AccountService.ServiceName, false));
        foreach (var section in Sections)
            section.SelectCommand = new RelayCommand<SettingsNavItemViewModel>(SelectSection);
        Sections[0].IsSelected = true;
        Reload();
        LoadRemoteWorkspaces();
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
        new(SettingsSection.Remote, "远程"),
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
    private bool _isRemoteSection;

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

    public ObservableCollection<SshWorkspaceRowViewModel> RemoteWorkspaces { get; } = [];

    /** 右下角气泡回调(由 MainViewModel 注入): (标题, 正文, 是否错误)。 */
    public Action<string, string, bool>? Notify { get; set; }

    /** 远端目录选择器(由视图注入): 给定工作区与初始目录, 返回选中目录; 取消返回 null。 */
    public Func<SshWorkspace, string?, Task<string?>>? RemoteFolderPicker { get; set; }

    /** 远端缺少 dsharp 时自动下载并部署(仿 VS Code Server; 不注册 PATH 命令)。 */
    [ObservableProperty]
    private bool _remoteAutoDeploy = true;

    [ObservableProperty]
    private string _remoteReleaseBaseUrl = GuiSettings.DefaultReleaseBaseUrl;

    [ObservableProperty]
    private string _remoteReleaseVersion = GuiSettings.DefaultReleaseVersion;

    [ObservableProperty]
    private string _remoteDownloadProxy = "";

    internal RemoteDsharpInstaller.Options BuildDeployOptions()
        => new(RemoteReleaseBaseUrl, RemoteReleaseVersion, string.IsNullOrWhiteSpace(RemoteDownloadProxy) ? null : RemoteDownloadProxy);

    /** 远程连接类操作进行中(测试连接/浏览目录), 期间禁用按钮避免重入。 */
    [ObservableProperty]
    private bool _remoteBusy;

    partial void OnRemoteBusyChanged(bool value) => OnPropertyChanged(nameof(RemoteIdle));

    public bool RemoteIdle => !RemoteBusy;

    [ObservableProperty]
    private SshWorkspaceRowViewModel? _selectedRemoteWorkspace;

    [ObservableProperty]
    private bool _hasSelectedRemoteWorkspace;

    partial void OnSelectedRemoteWorkspaceChanged(SshWorkspaceRowViewModel? value)
        => HasSelectedRemoteWorkspace = value is not null;

    [RelayCommand]
    private void AddRemoteWorkspace()
    {
        var row = new SshWorkspaceRowViewModel { Name = "新远程工作区", Owner = this };
        RemoteWorkspaces.Add(row);
        SelectedRemoteWorkspace = row;
    }

    [RelayCommand]
    private void RemoveRemoteWorkspace(SshWorkspaceRowViewModel? row)
    {
        if (row is null)
            return;
        RemoteWorkspaces.Remove(row);
        if (ReferenceEquals(SelectedRemoteWorkspace, row))
            SelectedRemoteWorkspace = RemoteWorkspaces.FirstOrDefault();
    }

    [RelayCommand]
    private void SaveRemoteWorkspaces()
    {
        _gui.Save(_gui.Load() with
        {
            RemoteWorkspaces = [.. RemoteWorkspaces.Select(row => row.ToModel())],
            RemoteAutoDeploy = RemoteAutoDeploy,
            RemoteReleaseBaseUrl = RemoteReleaseBaseUrl,
            RemoteReleaseVersion = RemoteReleaseVersion,
            RemoteDownloadProxy = RemoteDownloadProxy,
        });
        Status = $"远程工作区已保存（{RemoteWorkspaces.Count} 个）";
    }

    private void LoadRemoteWorkspaces()
    {
        var snapshot = _gui.Load();
        RemoteAutoDeploy = snapshot.RemoteAutoDeploy;
        RemoteReleaseBaseUrl = snapshot.RemoteReleaseBaseUrl;
        RemoteReleaseVersion = snapshot.RemoteReleaseVersion;
        RemoteDownloadProxy = snapshot.RemoteDownloadProxy;
        RemoteWorkspaces.Clear();
        foreach (var workspace in snapshot.RemoteWorkspaces)
        {
            var row = SshWorkspaceRowViewModel.From(workspace);
            row.Owner = this;
            RemoteWorkspaces.Add(row);
        }

        SelectedRemoteWorkspace = RemoteWorkspaces.FirstOrDefault();
    }

    /** “测试连接”: 走真实 ssh 连接(必要时先自动部署 dsharp), 成功/失败都经右下角气泡反馈。 */
    internal async Task TestRemoteWorkspaceAsync(SshWorkspaceRowViewModel row)
    {
        if (RemoteBusy)
            return;
        RemoteBusy = true;
        try
        {
            Notify?.Invoke("测试连接", $"正在连接 {row.Summary}…", false);
            var workspace = row.ToModel();
            string? hostCommand = null;
            if (RemoteAutoDeploy)
                hostCommand = (await RemoteDsharpInstaller.EnsureAsync(workspace, BuildDeployOptions(), message => Notify?.Invoke("远端部署", message, false), CancellationToken.None)).CommandPath;
            Notify?.Invoke("连接成功", await SshRemoteWorkspaceLauncher.TestAsync(workspace, hostCommand, CancellationToken.None), false);
        }
        catch (Exception error)
        {
            Notify?.Invoke("连接失败", error.Message, true);
        }
        finally
        {
            RemoteBusy = false;
        }
    }

    /** “选择远端目录”: 先连上远端, 再由选择的对话框浏览远端目录树。 */
    internal async Task BrowseRemotePathAsync(SshWorkspaceRowViewModel row)
    {
        if (RemoteBusy || RemoteFolderPicker is null)
            return;
        RemoteBusy = true;
        try
        {
            var picked = await RemoteFolderPicker(row.ToModel(), row.RemotePath);
            if (!string.IsNullOrEmpty(picked))
                row.RemotePath = picked;
        }
        catch (Exception error)
        {
            Notify?.Invoke("选择远端目录失败", error.Message, true);
        }
        finally
        {
            RemoteBusy = false;
        }
    }

    private void SelectSection(SettingsNavItemViewModel? section)
    {
        if (section is null)
            return;
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
        IsRemoteSection = Section == SettingsSection.Remote;
        IsAboutSection = Section == SettingsSection.About;
        if (Section == SettingsSection.Model)
            LoadHarnessSettings();
        if (Section == SettingsSection.Plugins)
            LoadPluginRows();
        if (Section == SettingsSection.Account)
            _ = Account.RefreshAsync();
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
            Providers.Add(new ProviderRowViewModel(providerName, ProviderTypes.Canonical(providerEntry.Type), providerEntry.Options?.BaseUrl ?? "", providerEntry.Models.Count));
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

public sealed class PluginRowViewModel(string package, string description, bool enabled) : ObservableObject
{
    public string Package { get; } = package;

    public string Description { get; } = description;

    public bool Enabled { get; } = enabled;

    public string ActionLabel => Enabled ? "禁用" : "启用";

    public string WireAction => $"{(Enabled ? "disable" : "enable")}:{Package}";
}

/** “远程”分页的一行: 可编辑字段, 保存时转回 SshWorkspace。 */
public sealed partial class SshWorkspaceRowViewModel : ObservableObject
{
    public static IReadOnlyList<string> AuthModes { get; } = [SshAuth.Password, SshAuth.Key, SshAuth.Agent];

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _host = "";

    [ObservableProperty]
    private string _port = "22";

    [ObservableProperty]
    private string _user = "";

    [ObservableProperty]
    private string _auth = SshAuth.Agent;

    [ObservableProperty]
    private string? _password;

    [ObservableProperty]
    private string? _keyPath;

    [ObservableProperty]
    private string? _proxy;

    [ObservableProperty]
    private string? _remotePath;

    public bool IsPassword => Auth == SshAuth.Password;

    public bool IsKey => Auth == SshAuth.Key;

    public string Summary => $"{User}@{Host}:{Port}";

    /** 所属设置页: 供“测试连接/选择远端目录”回连父级(连接、通知与选择器都在父级)。 */
    public SettingsViewModel? Owner { get; set; }

    [RelayCommand]
    private Task TestConnectionAsync() => Owner?.TestRemoteWorkspaceAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task BrowseRemotePathAsync() => Owner?.BrowseRemotePathAsync(this) ?? Task.CompletedTask;

    partial void OnAuthChanged(string value)
    {
        OnPropertyChanged(nameof(IsPassword));
        OnPropertyChanged(nameof(IsKey));
    }

    partial void OnUserChanged(string value) => OnPropertyChanged(nameof(Summary));

    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(Summary));

    partial void OnPortChanged(string value) => OnPropertyChanged(nameof(Summary));

    public SshWorkspace ToModel() => new(
        Name,
        Host,
        int.TryParse(Port, out var port) ? port : 22,
        User,
        Auth,
        KeyPath,
        Proxy,
        RemotePath,
        Password);

    public static SshWorkspaceRowViewModel From(SshWorkspace workspace) => new()
    {
        Name = workspace.Name,
        Host = workspace.Host,
        Port = workspace.Port.ToString(),
        User = workspace.User,
        Auth = workspace.Auth,
        Password = workspace.Password,
        KeyPath = workspace.KeyPath,
        Proxy = workspace.Proxy,
        RemotePath = workspace.RemotePath,
    };
}
