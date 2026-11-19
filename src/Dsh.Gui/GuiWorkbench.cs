using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
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

    public string? Title { get; } = title;

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
    private readonly List<PageRegistration> _pages = [];
    private readonly List<ShellSlotRegistration> _toolbar = [];
    private readonly List<ShellSlotRegistration> _settingsPanels = [];
    private readonly List<SessionSlotRegistration> _chatHeader = [];
    private readonly List<SessionSlotRegistration> _chatBanners = [];
    private readonly List<SessionSlotRegistration> _statusItems = [];
    private readonly List<TemplateRegistration> _templates = [];
    private MainViewModel? _viewModel;
    private Window? _window;
    private string? _evictedPageId;
    private bool _disposed;

    public ObservableCollection<GuiSlotItem> ToolbarItems { get; } = [];

    public ObservableCollection<GuiSlotItem> ChatHeaderItems { get; } = [];

    public ObservableCollection<GuiSlotItem> ChatBanners { get; } = [];

    public ObservableCollection<GuiSlotItem> StatusItems { get; } = [];

    public ObservableCollection<GuiSlotItem> SettingsPanels { get; } = [];

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
        var registration = new PageRegistration(id, label, icon, factory);
        lock (_gate)
        {
            if (_disposed)
                return EmptyDisposable.Instance;
            _pages.Add(registration);
        }
        RunOnUi(() => ApplyPages());
        return new UnregisterHandle(() => RemovePage(registration));
    }

    /** Registers or replaces a control in the top workspace bar; disposing detaches and scrubs it. */
    public IDisposable RegisterToolbarItem(string id, Func<GuiShellContext, Control> factory)
        => RegisterShellSlot(_toolbar, ToolbarItems, nameof(HasToolbarItems), id, null, factory);

    /** Registers or replaces a plugin configuration panel inside the Plugins settings section. */
    public IDisposable RegisterSettingsPanel(string id, string title, Func<GuiShellContext, Control> factory)
        => RegisterShellSlot(_settingsPanels, SettingsPanels, nameof(HasSettingsPanels), id, title, factory);

    /** Registers or replaces an action or indicator control in the chat session header. */
    public IDisposable RegisterChatHeaderItem(string id, Func<GuiSessionContext, Control> factory)
        => RegisterSessionSlot(_chatHeader, ChatHeaderItems, nameof(HasChatHeaderItems), id, factory);

    /** Registers or replaces a session banner panel between the chat tab strip and transcript. */
    public IDisposable RegisterChatBanner(string id, Func<GuiSessionContext, Control> factory)
        => RegisterSessionSlot(_chatBanners, ChatBanners, nameof(HasChatBanners), id, factory);

    /** Registers or replaces an indicator control in the chat bottom status bar. */
    public IDisposable RegisterStatusItem(string id, Func<GuiSessionContext, Control> factory)
        => RegisterSessionSlot(_statusItems, StatusItems, nameof(HasStatusItems), id, factory);

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
        RunOnUi(() => AttachTemplate(registration));
        return new UnregisterHandle(() => RemoveTemplate(registration));
    }

    internal void AttachViewModel(MainViewModel viewModel)
    {
        MainViewModel? previous;
        lock (_gate)
        {
            previous = _viewModel;
            _viewModel = viewModel;
        }
        if (previous is not null && !ReferenceEquals(previous, viewModel))
            previous.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RunOnUi(RebuildAll);
    }

    internal void DetachViewModel(MainViewModel viewModel)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_viewModel, viewModel))
                return;
            _viewModel = null;
            _evictedPageId = null;
        }
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        RunOnUi(ClearAllSlots);
    }

    internal void AttachWindow(Window window)
    {
        TemplateRegistration[] templates;
        lock (_gate)
        {
            _window = window;
            templates = [.. _templates];
        }
        RunOnUi(() =>
        {
            foreach (var template in templates)
                if (!window.DataTemplates.Contains(template.Wrapper))
                    window.DataTemplates.Add(template.Wrapper);
        });
    }

    internal void DetachWindow(Window window)
    {
        TemplateRegistration[] templates;
        lock (_gate)
        {
            if (!ReferenceEquals(_window, window))
                return;
            _window = null;
            templates = [.. _templates];
        }
        foreach (var template in templates)
            window.DataTemplates.Remove(template.Wrapper);
    }

    /** Refreshes session-scoped controls whenever the active chat session switches. */
    internal void NotifySessionChanged()
    {
        RunOnUi(() =>
        {
            RebuildSessionList(_chatHeader, ChatHeaderItems, nameof(HasChatHeaderItems), forceRecreate: true);
            RebuildSessionList(_chatBanners, ChatBanners, nameof(HasChatBanners), forceRecreate: true);
            RebuildSessionList(_statusItems, StatusItems, nameof(HasStatusItems), forceRecreate: true);
        });
    }

    /** Refreshes shell-scoped controls when the current workspace path changes. */
    internal void NotifyWorkspaceChanged()
    {
        RunOnUi(() =>
        {
            RebuildShellList(_toolbar, ToolbarItems, nameof(HasToolbarItems), forceRecreate: true);
            RebuildShellList(_settingsPanels, SettingsPanels, nameof(HasSettingsPanels), forceRecreate: true);
        });
    }

    public void Dispose()
    {
        MainViewModel? viewModel;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            viewModel = _viewModel;
            _viewModel = null;
            _window = null;
            _evictedPageId = null;
            DetachAllRegistrations();
        }
        if (viewModel is not null)
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void DetachAllRegistrations()
    {
        foreach (var page in _pages)
            page.Detach();
        foreach (var slot in _toolbar.Concat(_settingsPanels))
            slot.Detach();
        foreach (var slot in _chatHeader.Concat(_chatBanners).Concat(_statusItems))
            slot.Detach();
        foreach (var template in _templates)
            template.Detach();
        _pages.Clear();
        _toolbar.Clear();
        _settingsPanels.Clear();
        _chatHeader.Clear();
        _chatBanners.Clear();
        _statusItems.Clear();
        _templates.Clear();
    }

    private IDisposable RegisterShellSlot(
        List<ShellSlotRegistration> list,
        ObservableCollection<GuiSlotItem> target,
        string flagProperty,
        string id,
        string? title,
        Func<GuiShellContext, Control> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        var registration = new ShellSlotRegistration(id, title, factory);
        lock (_gate)
        {
            if (_disposed)
                return EmptyDisposable.Instance;
            list.Add(registration);
        }
        RunOnUi(() => RebuildShellList(list, target, flagProperty));
        return new UnregisterHandle(() => RemoveShellSlot(list, target, flagProperty, registration));
    }

    private IDisposable RegisterSessionSlot(
        List<SessionSlotRegistration> list,
        ObservableCollection<GuiSlotItem> target,
        string flagProperty,
        string id,
        Func<GuiSessionContext, Control> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        var registration = new SessionSlotRegistration(id, factory);
        lock (_gate)
        {
            if (_disposed)
                return EmptyDisposable.Instance;
            list.Add(registration);
        }
        RunOnUi(() => RebuildSessionList(list, target, flagProperty));
        return new UnregisterHandle(() => RemoveSessionSlot(list, target, flagProperty, registration));
    }

    private void RemovePage(PageRegistration registration)
    {
        registration.Detach();
        PageRegistration? fallback;
        lock (_gate)
        {
            _pages.Remove(registration);
            fallback = _pages.LastOrDefault(item => item.Id == registration.Id);
        }
        RunOnUi(() =>
        {
            if (CurrentViewModel() is not { } viewModel)
                return;
            if (fallback is not null)
            {
                InstallPage(viewModel, fallback);
                return;
            }
            var wasActive = viewModel.Workspace.ActivePage?.Id == registration.Id;
            var inView = viewModel.Page == AppPage.WorkspacePage && wasActive;
            if (wasActive)
                ScrubObject(viewModel.Workspace.PageContent);
            if (inView)
                _evictedPageId = registration.Id;
            viewModel.Workspace.RemoveContributedPage(registration.Id, returnToDashboard: inView);
            if (wasActive)
                FlushDetachedLayout();
        });
    }

    private void RemoveShellSlot(
        List<ShellSlotRegistration> list,
        ObservableCollection<GuiSlotItem> target,
        string flagProperty,
        ShellSlotRegistration registration)
    {
        registration.Detach();
        lock (_gate)
            list.Remove(registration);
        RunOnUi(() => RebuildShellList(list, target, flagProperty));
    }

    private void RemoveSessionSlot(
        List<SessionSlotRegistration> list,
        ObservableCollection<GuiSlotItem> target,
        string flagProperty,
        SessionSlotRegistration registration)
    {
        registration.Detach();
        lock (_gate)
            list.Remove(registration);
        RunOnUi(() => RebuildSessionList(list, target, flagProperty));
    }

    private void AttachTemplate(TemplateRegistration registration)
    {
        Window? window;
        lock (_gate)
            window = _window;
        if (window is not null && !window.DataTemplates.Contains(registration.Wrapper))
            window.DataTemplates.Add(registration.Wrapper);
    }

    private void RemoveTemplate(TemplateRegistration registration)
    {
        registration.Detach();
        Window? window;
        lock (_gate)
        {
            _templates.Remove(registration);
            window = _window;
        }
        RunOnUi(() => window?.DataTemplates.Remove(registration.Wrapper));
    }

    private void RebuildAll()
    {
        ClearAllSlots();
        ApplyPages();
        RebuildShellList(_toolbar, ToolbarItems, nameof(HasToolbarItems), forceRecreate: true);
        RebuildShellList(_settingsPanels, SettingsPanels, nameof(HasSettingsPanels), forceRecreate: true);
        RebuildSessionList(_chatHeader, ChatHeaderItems, nameof(HasChatHeaderItems), forceRecreate: true);
        RebuildSessionList(_chatBanners, ChatBanners, nameof(HasChatBanners), forceRecreate: true);
        RebuildSessionList(_statusItems, StatusItems, nameof(HasStatusItems), forceRecreate: true);
    }

    private void ClearAllSlots()
    {
        ClearSlotCollection(ToolbarItems, nameof(HasToolbarItems));
        ClearSlotCollection(SettingsPanels, nameof(HasSettingsPanels));
        ClearSlotCollection(ChatHeaderItems, nameof(HasChatHeaderItems));
        ClearSlotCollection(ChatBanners, nameof(HasChatBanners));
        ClearSlotCollection(StatusItems, nameof(HasStatusItems));
    }

    private void ApplyPages()
    {
        if (CurrentViewModel() is not { } viewModel)
            return;
        PageRegistration[] effective;
        lock (_gate)
            effective = [.. _pages.GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.Last())];
        foreach (var page in effective)
            InstallPage(viewModel, page);
    }

    private void InstallPage(MainViewModel viewModel, PageRegistration page)
    {
        var icon = page.Icon ?? DefaultIcon();
        var entry = new WorkspacePageEntry(page.Id, page.Label, icon, page.Invoke);
        var wasActive = viewModel.Workspace.ActivePage?.Id == page.Id;
        var inView = viewModel.Page == AppPage.WorkspacePage && wasActive;
        var reopen = inView || (viewModel.Page == AppPage.Dashboard && _evictedPageId == page.Id);
        if (wasActive)
            ScrubObject(viewModel.Workspace.PageContent);
        viewModel.Workspace.Pages.Register(entry);
        if (reopen)
        {
            _evictedPageId = null;
            viewModel.Workspace.OpenPage(entry);
            if (wasActive)
                FlushDetachedLayout();
            return;
        }
        if (wasActive)
        {
            viewModel.Workspace.PageContent = null;
            viewModel.Workspace.ActivePage = null;
            FlushDetachedLayout();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Page) && sender is MainViewModel viewModel && viewModel.Page != AppPage.Dashboard)
            _evictedPageId = null;
    }

    private void RebuildShellList(
        List<ShellSlotRegistration> source,
        ObservableCollection<GuiSlotItem> target,
        string flagProperty,
        bool forceRecreate = false)
    {
        if (CurrentViewModel() is not { } viewModel)
        {
            ClearSlotCollection(target, flagProperty);
            return;
        }
        ShellSlotRegistration[] effective;
        lock (_gate)
            effective = [.. source.GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.Last())];
        var context = viewModel.CreateShellContext();
        SyncSlots(
            target,
            effective.Select(item => (item.Id, item.Title, (object)item, (Func<Control?>)(() => item.CreateControl(context)))).ToArray(),
            flagProperty,
            forceRecreate);
    }

    private void RebuildSessionList(
        List<SessionSlotRegistration> source,
        ObservableCollection<GuiSlotItem> target,
        string flagProperty,
        bool forceRecreate = false)
    {
        if (CurrentViewModel() is not { } viewModel)
        {
            ClearSlotCollection(target, flagProperty);
            return;
        }
        SessionSlotRegistration[] effective;
        lock (_gate)
            effective = [.. source.GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.Last())];
        var context = viewModel.CreateSessionContext();
        SyncSlots(
            target,
            effective.Select(item => (item.Id, (string?)null, (object)item, (Func<Control?>)(() => item.CreateControl(context)))).ToArray(),
            flagProperty,
            forceRecreate);
    }

    private void SyncSlots(
        ObservableCollection<GuiSlotItem> target,
        IReadOnlyList<(string Id, string? Title, object Owner, Func<Control?> Factory)> desired,
        string flagProperty,
        bool forceRecreate)
    {
        var detached = false;
        var desiredIds = desired.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (desiredIds.Contains(target[index].Id))
                continue;
            var removed = target[index];
            var old = removed.Content;
            removed.Content = null;
            removed.Owner = null;
            ScrubControl(old);
            target.RemoveAt(index);
            detached = true;
        }
        for (var index = 0; index < desired.Count; index++)
        {
            var (id, title, owner, factory) = desired[index];
            var existing = target.FirstOrDefault(item => item.Id == id);
            if (existing is null)
            {
                target.Insert(index, new GuiSlotItem(id, title) { Owner = owner, Content = factory() });
                continue;
            }
            if (forceRecreate || !ReferenceEquals(existing.Owner, owner))
            {
                var previous = existing.Content;
                existing.Owner = owner;
                existing.Content = factory();
                if (!ReferenceEquals(previous, existing.Content))
                {
                    ScrubControl(previous);
                    detached = true;
                }
            }
            var currentIndex = target.IndexOf(existing);
            if (currentIndex != index)
                target.Move(currentIndex, index);
        }
        OnPropertyChanged(flagProperty);
        if (detached)
            FlushDetachedLayout();
    }

    private void ClearSlotCollection(ObservableCollection<GuiSlotItem> target, string flagProperty)
    {
        var hadItems = target.Count > 0;
        foreach (var item in target)
        {
            var old = item.Content;
            item.Content = null;
            item.Owner = null;
            ScrubControl(old);
        }
        target.Clear();
        OnPropertyChanged(flagProperty);
        if (hadItems)
            FlushDetachedLayout();
    }

    private void FlushDetachedLayout()
    {
        Window? window;
        lock (_gate)
            window = _window;
        if (window is { IsVisible: true })
            window.UpdateLayout();
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private MainViewModel? CurrentViewModel()
    {
        lock (_gate)
            return _viewModel is { IsDisposed: false } active ? active : null;
    }

    private void RunOnUi(Action action)
    {
        bool attached;
        lock (_gate)
            attached = _viewModel is not null || _window is not null;
        if (!attached || Application.Current is null)
            return;
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        var operation = Dispatcher.UIThread.InvokeAsync(action);
        operation.GetTask().Wait(TimeSpan.FromMilliseconds(500));
    }

    private void ScrubObject(object? value)
    {
        if (value is Control control)
        {
            ScrubControl(control);
            return;
        }
        (value as IDisposable)?.Dispose();
    }

    private void ScrubControl(Control? root)
    {
        if (root is null)
            return;
        Window? window;
        lock (_gate)
            window = _window;
        if (window?.FocusManager?.GetFocusedElement() is Visual focused
            && (ReferenceEquals(focused, root) || root.IsVisualAncestorOf(focused)))
            window.FocusManager.Focus(null);
        var nodes = root.GetVisualDescendants().OfType<Visual>().Prepend(root).ToArray();
        foreach (var node in nodes)
            DetachNodeReferences(node);
    }

    private static void DetachNodeReferences(Visual node)
    {
        IDisposable? disposableContext = null;
        if (node is StyledElement styled)
        {
            disposableContext = styled.DataContext as IDisposable;
            styled.DataContext = null;
        }
        if (node is Control control)
        {
            control.Tag = null;
            ToolTip.SetTip(control, null);
            control.ContextFlyout = null;
        }
        if (node is Button button)
        {
            button.Command = null;
            button.CommandParameter = null;
            button.Flyout = null;
        }
        if (node is MenuItem menuItem)
        {
            menuItem.Command = null;
            menuItem.CommandParameter = null;
        }
        disposableContext?.Dispose();
        (node as IDisposable)?.Dispose();
    }

    private static Geometry DefaultIcon()
    {
        if (Application.Current is { } app && app.TryFindResource("Icon.Plugin", out var resource) && resource is Geometry geometry)
            return geometry;
        return StreamGeometry.Parse(DefaultPluginIconPath);
    }

    private sealed class PageRegistration(string id, string label, Geometry? icon, Func<WorkspacePageContext, object> factory)
    {
        private Func<WorkspacePageContext, object>? _factory = factory;

        public string Id { get; } = id;
        public string Label { get; } = label;
        public Geometry? Icon { get; } = icon;

        public object Invoke(WorkspacePageContext context)
            => _factory?.Invoke(context) ?? new TextBlock { Text = "插件页面已卸载" };

        public void Detach() => _factory = null;
    }

    private sealed class ShellSlotRegistration(string id, string? title, Func<GuiShellContext, Control> factory)
    {
        private Func<GuiShellContext, Control>? _factory = factory;

        public string Id { get; } = id;
        public string? Title { get; } = title;

        public Control? CreateControl(GuiShellContext context) => _factory?.Invoke(context);
        public void Detach() => _factory = null;
    }

    private sealed class SessionSlotRegistration(string id, Func<GuiSessionContext, Control> factory)
    {
        private Func<GuiSessionContext, Control>? _factory = factory;

        public string Id { get; } = id;

        public Control? CreateControl(GuiSessionContext context) => _factory?.Invoke(context);
        public void Detach() => _factory = null;
    }

    private sealed class TemplateRegistration(IDataTemplate inner)
    {
        public RemovableDataTemplate Wrapper { get; } = new(inner);
        public void Detach() => Wrapper.Detach();
    }

    private sealed class RemovableDataTemplate(IDataTemplate inner) : IDataTemplate
    {
        private IDataTemplate? _inner = inner;

        public Control? Build(object? param) => _inner?.Build(param);
        public bool Match(object? data) => _inner?.Match(data) == true;
        public void Detach() => _inner = null;
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
