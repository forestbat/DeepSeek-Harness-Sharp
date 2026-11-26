using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui;

/** Host shell actions and active workspace metadata exposed to partial GUI plugin contributions. */
public readonly record struct GuiShellContext(
    string Machine,
    string WorkspacePath,
    Action<string> OpenPage,
    Action<string, string, bool> ShowToast,
    Func<string, Task<string>> RunCommandAsync);

/** Active chat session metadata and command runner exposed to session-scoped GUI plugin contributions. */
public readonly record struct GuiSessionContext(
    string Machine,
    string SessionId,
    string WorkspacePath,
    Func<string, Task<string>> RunCommandAsync,
    Action<string> SetStatus);

/** Host-owned wrapper for a plugin-contributed control so detaching drops all plugin references immediately. */
public sealed class GuiSlotItem(string id, string? title = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = id;

    public string? Title
    {
        get;
        internal set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTitle)));
        }
    } = title;

    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

    internal object? Owner { get; set; }

    public Control? Content
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
        }
    }
}

/** Central registry for partial GUI extensions contributed by active plugins in the GUI process. */
public sealed class GuiWorkbench : INotifyPropertyChanged, IDisposable
{
    public const string ServiceName = "guiWorkbench";

    public event PropertyChangedEventHandler? PropertyChanged;

    private const string DefaultPluginIconPath = "M12 2a2.5 2.5 0 0 1 2.5 2.5V6h3A1.5 1.5 0 0 1 19 7.5v3h1.5a2.5 2.5 0 0 1 0 5H19v3a1.5 1.5 0 0 1-1.5 1.5h-3v-1.5a2.5 2.5 0 0 0-5 0V20h-3A1.5 1.5 0 0 1 5 18.5v-3H3.5a2.5 2.5 0 0 1 0-5H5v-3A1.5 1.5 0 0 1 6.5 6h3V4.5A2.5 2.5 0 0 1 12 2Z";

    private readonly Lock _gate = new();
    private readonly RegistrationSet<PageRegistration> _pages = new();
    private readonly SlotGroup<GuiShellContext> _toolbar = new(nameof(HasToolbarItems));
    private readonly SlotGroup<GuiShellContext> _settingsPanels = new(nameof(HasSettingsPanels));
    private readonly SlotGroup<GuiSessionContext> _chatHeader = new(nameof(HasChatHeaderItems));
    private readonly SlotGroup<GuiSessionContext> _chatBanners = new(nameof(HasChatBanners));
    private readonly SlotGroup<GuiSessionContext> _statusItems = new(nameof(HasStatusItems));
    private readonly List<TemplateRegistration> _templates = [];
    private MainViewModel? _viewModel;
    private Window? _window;
    private bool _disposed;
    private int _refreshScheduled;

    private MainViewModel? _appliedViewModel;
    private Window? _appliedWindow;
    private readonly Dictionary<string, (PageRegistration Registration, WorkspacePageEntry Entry)> _appliedPages = new(StringComparer.Ordinal);
    private readonly HashSet<TemplateRegistration> _appliedTemplates = [];
    private int _appliedPageVersion = -1;
    private string? _evictedPageId;
    private bool _refreshing;
    private bool _refreshAgain;

    public ObservableCollection<GuiSlotItem> ToolbarItems => _toolbar.Items;

    public ObservableCollection<GuiSlotItem> ChatHeaderItems => _chatHeader.Items;

    public ObservableCollection<GuiSlotItem> ChatBanners => _chatBanners.Items;

    public ObservableCollection<GuiSlotItem> StatusItems => _statusItems.Items;

    public ObservableCollection<GuiSlotItem> SettingsPanels => _settingsPanels.Items;

    public bool HasToolbarItems => ToolbarItems.Count > 0;

    public bool HasChatHeaderItems => ChatHeaderItems.Count > 0;

    public bool HasChatBanners => ChatBanners.Count > 0;

    public bool HasStatusItems => StatusItems.Count > 0;

    public bool HasSettingsPanels => SettingsPanels.Count > 0;

    /** Registers or replaces a navigation page in the workspace sidebar; disposing removes the page. */
    public IDisposable RegisterPage(string id, string label, Func<WorkspacePageContext, object> factory, Geometry? icon = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(factory);
        return Register(_pages, id, new PageRegistration(id, label, icon, factory));
    }

    /** Registers or replaces a control in the top workspace bar; disposing detaches and scrubs it. */
    public IDisposable RegisterToolbarItem(string id, Func<GuiShellContext, Control> factory)
        => RegisterSlot(_toolbar, id, null, factory);

    /** Registers or replaces a plugin configuration panel inside the Plugins settings section. */
    public IDisposable RegisterSettingsPanel(string id, string title, Func<GuiShellContext, Control> factory)
        => RegisterSlot(_settingsPanels, id, title, factory);

    /** Registers or replaces an action or indicator control in the chat session header. */
    public IDisposable RegisterChatHeaderItem(string id, Func<GuiSessionContext, Control> factory)
        => RegisterSlot(_chatHeader, id, null, factory);

    /** Registers or replaces a session banner panel between the chat tab strip and transcript. */
    public IDisposable RegisterChatBanner(string id, Func<GuiSessionContext, Control> factory)
        => RegisterSlot(_chatBanners, id, null, factory);

    /** Registers or replaces an indicator control in the chat bottom status bar. */
    public IDisposable RegisterStatusItem(string id, Func<GuiSessionContext, Control> factory)
        => RegisterSlot(_statusItems, id, null, factory);

    /** Registers a window-level data template so plugin view models render without hardcoding views in host XAML. */
    public IDisposable RegisterDataTemplate(IDataTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var registration = new TemplateRegistration(template);
        lock (_gate)
        {
            if (_disposed)
                return EmptyDisposable.Instance;
            _templates.Add(registration);
        }
        RefreshUi();
        return new UnregisterHandle(() =>
        {
            lock (_gate)
            {
                _templates.Remove(registration);
                registration.Detach();
            }
            RefreshUi();
        });
    }

    internal void AttachViewModel(MainViewModel viewModel)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _viewModel = viewModel;
        }
        RefreshUi();
    }

    internal void DetachViewModel(MainViewModel viewModel)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_viewModel, viewModel))
                return;
            _viewModel = null;
        }
        RefreshUi();
    }

    internal void AttachWindow(Window window)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _window = window;
        }
        RefreshUi();
    }

    internal void DetachWindow(Window window)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_window, window))
                return;
            _window = null;
        }
        RefreshUi();
    }

    internal void NotifySessionChanged() => RefreshUi();

    internal void NotifyWorkspaceChanged() => RefreshUi();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _viewModel = null;
            _window = null;
            _pages.Clear();
            _toolbar.Registrations.Clear();
            _settingsPanels.Registrations.Clear();
            _chatHeader.Registrations.Clear();
            _chatBanners.Registrations.Clear();
            _statusItems.Registrations.Clear();
            foreach (var template in _templates)
                template.Detach();
            _templates.Clear();
        }
        RefreshUi();
    }

    private IDisposable RegisterSlot<TContext>(SlotGroup<TContext> group, string id, string? title, Func<TContext, Control> factory)
        where TContext : struct
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        return Register(group.Registrations, id, new SlotRegistration<TContext>(id, title, factory));
    }

    private IDisposable Register<T>(RegistrationSet<T> registrations, string id, T registration) where T : Registration
    {
        lock (_gate)
        {
            if (_disposed)
                return EmptyDisposable.Instance;
            registrations.Add(id, registration);
        }
        RefreshUi();
        return new UnregisterHandle(() =>
        {
            lock (_gate)
            {
                registrations.Remove(id, registration);
                registration.Detach();
            }
            RefreshUi();
        });
    }

    private void RefreshUi()
    {
        if (Application.Current is null)
            return;
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyChanges();
            return;
        }
        if (Interlocked.Exchange(ref _refreshScheduled, 1) == 0)
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _refreshScheduled, 0);
                try
                {
                    ApplyChanges();
                }
                catch (Exception error)
                {
                    var message = Dsh.Runtime.OutboundProxy.Redact(error.Message);
                    Trace.TraceError($"Plugin GUI update failed: {message}");
                    _appliedViewModel?.ShowToast("插件界面更新失败", message, true);
                }
            });
    }

    private void ApplyChanges()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                Reconcile();
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Reconcile()
    {
        MainViewModel? viewModel;
        Window? window;
        TemplateRegistration[] templates;
        lock (_gate)
        {
            viewModel = _viewModel;
            window = _window;
            templates = window is null ? [] : [.. _templates];
        }
        if (!ReferenceEquals(_appliedViewModel, viewModel))
            BindViewModel(viewModel);
        ApplyTemplates(window, templates);
        if (viewModel is not { IsDisposed: false })
            return;
        ApplyPages(viewModel);
        var shell = viewModel.CreateShellContext();
        ApplySlots(_toolbar, shell);
        ApplySlots(_settingsPanels, shell);
        var session = viewModel.CreateSessionContext();
        ApplySlots(_chatHeader, session);
        ApplySlots(_chatBanners, session);
        ApplySlots(_statusItems, session);
    }

    private void BindViewModel(MainViewModel? viewModel)
    {
        if (_appliedViewModel is { } previous)
        {
            previous.PropertyChanged -= OnViewModelPropertyChanged;
            foreach (var (id, page) in _appliedPages)
                ChangePage(previous, id, page.Entry, null, reopen: false);
            _appliedPages.Clear();
            ClearSlots(_toolbar);
            ClearSlots(_settingsPanels);
            ClearSlots(_chatHeader);
            ClearSlots(_chatBanners);
            ClearSlots(_statusItems);
        }
        _appliedViewModel = viewModel;
        _appliedPageVersion = -1;
        _evictedPageId = null;
        if (viewModel is not null)
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void ApplyTemplates(Window? window, TemplateRegistration[] templates)
    {
        if (!ReferenceEquals(_appliedWindow, window))
        {
            if (_appliedWindow is { } previous)
                foreach (var registration in _appliedTemplates)
                    previous.DataTemplates.Remove(registration.Wrapper);
            _appliedTemplates.Clear();
            _appliedWindow = window;
        }
        if (window is null)
            return;
        var desired = templates.ToHashSet();
        foreach (var registration in _appliedTemplates.Where(item => !desired.Contains(item)).ToArray())
        {
            window.DataTemplates.Remove(registration.Wrapper);
            _appliedTemplates.Remove(registration);
        }
        foreach (var registration in templates)
            if (_appliedTemplates.Add(registration))
                window.DataTemplates.Add(registration.Wrapper);
    }

    private void ApplyPages(MainViewModel viewModel)
    {
        PageRegistration[] effective;
        int version;
        lock (_gate)
        {
            version = _pages.Version;
            if (_appliedPageVersion == version)
                return;
            effective = _pages.Snapshot();
        }
        var ids = effective.Select(page => page.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, previous) in _appliedPages.Where(item => !ids.Contains(item.Key)).ToArray())
        {
            ChangePage(viewModel, id, previous.Entry, null);
            _appliedPages.Remove(id);
        }
        foreach (var registration in effective)
        {
            var previous = _appliedPages.GetValueOrDefault(registration.Id);
            if (ReferenceEquals(previous.Registration, registration))
                continue;
            var entry = new WorkspacePageEntry(registration.Id, registration.Label, registration.Icon ?? DefaultIcon(), registration.Invoke)
            {
                DetachContent = DetachPageContent,
            };
            ChangePage(viewModel, registration.Id, previous.Entry, entry);
            _appliedPages[registration.Id] = (registration, entry);
        }
        _appliedPageVersion = version;
    }

    private void ChangePage(MainViewModel viewModel, string id, WorkspacePageEntry? previous, WorkspacePageEntry? next, bool reopen = true)
    {
        var workspace = viewModel.Workspace;
        var wasActive = workspace.ActivePage is { } active && (previous is null ? active.Id == id : ReferenceEquals(active, previous));
        var inView = wasActive && viewModel.Page == AppPage.WorkspacePage;
        var replacement = next ?? workspace.Pages.FindDefault(id);
        if (replacement is null)
        {
            if (inView && reopen)
                _evictedPageId = id;
            if (wasActive)
                workspace.RemoveContributedPage(id, returnToDashboard: inView && reopen);
            else
                workspace.Pages.Remove(id);
            return;
        }
        if (wasActive)
        {
            workspace.PageContent = null;
            workspace.ActivePage = null;
        }
        workspace.Pages.Register(replacement);
        if (reopen && (inView || (viewModel.Page == AppPage.Dashboard && _evictedPageId == id)))
        {
            _evictedPageId = null;
            workspace.OpenPage(replacement);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Page) && sender is MainViewModel viewModel && viewModel.Page != AppPage.Dashboard)
            _evictedPageId = null;
    }

    private void ApplySlots<TContext>(SlotGroup<TContext> group, TContext context) where TContext : struct
    {
        SlotRegistration<TContext>[] effective;
        int version;
        var contextChanged = !EqualityComparer<TContext?>.Default.Equals(group.Context, context);
        lock (_gate)
        {
            version = group.Registrations.Version;
            if (!contextChanged && group.Version == version)
                return;
            effective = group.Registrations.Snapshot();
        }
        SyncSlots(group, effective, context, contextChanged);
        group.Context = context;
        group.Version = version;
    }

    private void SyncSlots<TContext>(SlotGroup<TContext> group, SlotRegistration<TContext>[] desired, TContext context, bool contextChanged)
        where TContext : struct
    {
        var hadItems = group.Items.Count > 0;
        var remaining = desired.ToDictionary(registration => registration.Id, StringComparer.Ordinal);
        for (var index = group.Items.Count - 1; index >= 0; index--)
        {
            var item = group.Items[index];
            if (remaining.Remove(item.Id, out var registration))
                UpdateSlot(item, registration, context, contextChanged);
            else
            {
                ReleaseSlot(item);
                group.Items.RemoveAt(index);
            }
        }
        foreach (var registration in desired)
        {
            if (!remaining.ContainsKey(registration.Id))
                continue;
            var item = new GuiSlotItem(registration.Id, registration.Title);
            UpdateSlot(item, registration, context, contextChanged: true);
            group.Items.Add(item);
        }
        if (hadItems != (group.Items.Count > 0))
            OnPropertyChanged(group.FlagProperty);
    }

    private static void UpdateSlot<TContext>(GuiSlotItem item, SlotRegistration<TContext> registration, TContext context, bool contextChanged)
        where TContext : struct
    {
        if (!contextChanged && ReferenceEquals(item.Owner, registration))
            return;
        var content = registration.CreateControl(context);
        if (!ReferenceEquals(item.Content, content))
        {
            ReleaseSlot(item);
            item.Content = content;
        }
        item.Owner = registration;
        item.Title = registration.Title;
    }

    private void ClearSlots<TContext>(SlotGroup<TContext> group) where TContext : struct
    {
        group.Context = null;
        group.Version = -1;
        if (group.Items.Count == 0)
            return;
        foreach (var item in group.Items)
            ReleaseSlot(item);
        group.Items.Clear();
        OnPropertyChanged(group.FlagProperty);
    }

    private static void ReleaseSlot(GuiSlotItem item)
    {
        item.Owner = null;
        if (item.Content is not { } content)
            return;
        var presenter = content.GetVisualParent() as ContentPresenter;
        ClearFocus(content);
        item.Content = null;
        UpdateDetachedPresenter(presenter, content);
        ScrubControl(content);
    }

    private void DetachPageContent(object content)
    {
        var presenter = _appliedWindow?.FindControl<ContentControl>("WorkspacePageHost")?.Presenter;
        var root = content as Control;
        if (presenter is not null && ReferenceEquals(presenter.Content, content))
        {
            root = presenter.Child ?? root;
            if (root is not null)
                ClearFocus(root);
            presenter.SetCurrentValue(ContentPresenter.ContentProperty, null);
            UpdateDetachedPresenter(presenter, root);
        }
        if (root is not null)
            ScrubControl(root, content);
    }

    private static void UpdateDetachedPresenter(ContentPresenter? presenter, Control? child)
    {
        if (presenter is { Content: null, Child: not null } && ReferenceEquals(presenter.Child, child))
            presenter.UpdateChild();
    }

    private static void ClearFocus(Control root)
    {
        var focusManager = TopLevel.GetTopLevel(root)?.FocusManager;
        if (focusManager?.GetFocusedElement() is Visual focused
            && (ReferenceEquals(root, focused) || root.IsVisualAncestorOf(focused)))
            focusManager.Focus(null);
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static void ScrubControl(Control root, object? disposedByWorkspace = null)
    {
        var nodes = root.GetLogicalDescendants().OfType<StyledElement>()
            .Concat(root.GetVisualDescendants().OfType<StyledElement>())
            .Prepend(root).Distinct<StyledElement>(ReferenceEqualityComparer.Instance).ToArray();
        var owned = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);
        foreach (var node in nodes)
        {
            if (node.IsSet(StyledElement.DataContextProperty) && node.DataContext is IDisposable context)
                owned.Add(context);
            if (node is IDisposable disposable)
                owned.Add(disposable);
        }
        foreach (var node in nodes)
            DetachNodeReferences(node);
        foreach (var disposable in owned)
            if (!ReferenceEquals(disposable, disposedByWorkspace))
                disposable.Dispose();
    }

    private static void DetachNodeReferences(StyledElement node)
    {
        node.DataContext = null;
        node.Styles.Clear();
        node.Resources = new ResourceDictionary();
        if (node is not Control control)
            return;
        control.Tag = null;
        ToolTip.SetTip(control, null);
        control.ContextFlyout = null;
        DetachContainerReferences(control);
        if (control is Button button)
        {
            button.Command = null;
            button.CommandParameter = null;
            button.Flyout = null;
        }
        if (control is MenuItem menuItem)
        {
            menuItem.Command = null;
            menuItem.CommandParameter = null;
        }
    }

    private static void DetachContainerReferences(Control control)
    {
        if (control is ItemsControl items)
        {
            items.ItemsSource = null;
            items.Items.Clear();
            items.ItemTemplate = null;
        }
        if (control is ContentControl content)
        {
            content.Content = null;
            content.ContentTemplate = null;
        }
        if (control is Panel panel)
            panel.Children.Clear();
        if (control is Decorator decorator)
            decorator.Child = null;
    }

    private static Geometry DefaultIcon()
    {
        if (Application.Current is { } app && app.TryFindResource("Icon.Plugin", out var resource) && resource is Geometry geometry)
            return geometry;
        return StreamGeometry.Parse(DefaultPluginIconPath);
    }

    private abstract class Registration
    {
        public abstract void Detach();
    }

    private sealed class PageRegistration(string id, string label, Geometry? icon, Func<WorkspacePageContext, object> factory)
        : Registration
    {
        private Func<WorkspacePageContext, object>? _factory = factory;

        public string Id { get; } = id;
        public string Label { get; } = label;
        public Geometry? Icon { get; } = icon;

        public object? Invoke(WorkspacePageContext context) => Volatile.Read(ref _factory)?.Invoke(context);
        public override void Detach() => Volatile.Write(ref _factory, null);
    }

    private sealed class SlotRegistration<TContext>(string id, string? title, Func<TContext, Control> factory) : Registration
        where TContext : struct
    {
        private Func<TContext, Control>? _factory = factory;

        public string Id { get; } = id;
        public string? Title { get; } = title;

        public Control? CreateControl(TContext context) => Volatile.Read(ref _factory)?.Invoke(context);
        public override void Detach() => Volatile.Write(ref _factory, null);
    }

    private sealed class SlotGroup<TContext>(string flagProperty) where TContext : struct
    {
        public string FlagProperty { get; } = flagProperty;
        public ObservableCollection<GuiSlotItem> Items { get; } = [];
        public RegistrationSet<SlotRegistration<TContext>> Registrations { get; } = new();
        public TContext? Context { get; set; }
        public int Version { get; set; } = -1;
    }

    private sealed class RegistrationSet<T> where T : Registration
    {
        private readonly OrderedDictionary<string, List<T>> _byId = new(StringComparer.Ordinal);
        public int Version { get; private set; }

        public void Add(string id, T registration)
        {
            if (!_byId.TryGetValue(id, out var versions))
                _byId.Add(id, versions = []);
            versions.Add(registration);
            Version++;
        }

        public void Remove(string id, T registration)
        {
            if (!_byId.TryGetValue(id, out var versions))
                return;
            var wasEffective = ReferenceEquals(versions[^1], registration);
            versions.Remove(registration);
            if (versions.Count == 0)
                _byId.Remove(id);
            if (wasEffective)
                Version++;
        }

        public T[] Snapshot() => [.. _byId.Values.Select(versions => versions[^1])];

        public void Clear()
        {
            foreach (var versions in _byId.Values)
                foreach (var registration in versions)
                    registration.Detach();
            _byId.Clear();
            Version++;
        }
    }

    private sealed class TemplateRegistration(IDataTemplate inner)
    {
        public RemovableDataTemplate Wrapper { get; } = new(inner);
        public void Detach() => Wrapper.Detach();
    }

    private sealed class RemovableDataTemplate(IDataTemplate inner) : IDataTemplate
    {
        private IDataTemplate? _inner = inner;

        public Control? Build(object? param) => Volatile.Read(ref _inner)?.Build(param);
        public bool Match(object? data) => Volatile.Read(ref _inner)?.Match(data) == true;
        public void Detach() => Volatile.Write(ref _inner, null);
    }

    private sealed class UnregisterHandle(Action unregister) : IDisposable
    {
        private Action? _unregister = unregister;

        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _unregister, null);
            action?.Invoke();
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
