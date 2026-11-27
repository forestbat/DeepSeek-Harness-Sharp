using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Gui;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Host;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Plugins.Generator;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Dsh.Tests;

/** Verifies partial GUI plugin contributions, live hot-loading, in-place hot-reloading, dual-process sync, and ALC collection. */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class PluginGuiExtensibilityTests
{
    private const int CollectionRounds = 60;

    [Fact]
    public async Task Workbench_BackgroundRegistrationDoesNotWaitForUi_AndRestoresThePreviousSlot() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var window = new MainWindow(environment.App, await environment.OpenChatAsync());
        try
        {
            window.Show();
            var viewModel = window.ViewModel!;
            viewModel.ShowChatCommand.Execute(null);
            var workbench = viewModel.Workbench;
            var originalBuilds = 0;
            using var original = workbench.RegisterChatHeaderItem("overlap", context =>
            {
                originalBuilds++;
                return new Button { Content = context.SessionId };
            });
            Render(window);
            var initial = Assert.Single(workbench.ChatHeaderItems, item => item.Id == "overlap").Content;
            workbench.NotifySessionChanged();
            Assert.Same(initial, Assert.Single(workbench.ChatHeaderItems, item => item.Id == "overlap").Content);
            Assert.Equal(1, originalBuilds);

            // Keep the dispatcher occupied until the worker returns the registration handle.
            using var replacement = Task.Run(() => workbench.RegisterChatHeaderItem("overlap", _ =>
            {
                Assert.True(Dispatcher.UIThread.CheckAccess());
                return new Button { Content = "replacement" };
            })).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Assert.Same(initial, Assert.Single(workbench.ChatHeaderItems, item => item.Id == "overlap").Content);

            Render(window);
            var host = Assert.Single(window.GetVisualDescendants().OfType<ItemsControl>(), control => control.Name == "PluginChatHeaderHost");
            Assert.True(FindControlByText<Button>(host, "replacement").IsEffectivelyVisible);
            replacement.Dispose();
            Render(window);
            Assert.True(FindControlByText<Button>(host, viewModel.CurrentSessionId.Value).IsEffectivelyVisible);
            Assert.Equal(2, originalBuilds);
        }
        finally
        {
            window.Close();
            await window.ViewModel!.DisposeAsync();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public async Task Workbench_CoalescedSlotReloadUsesRegistrationOrder_AndKeepsOtherControls() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var window = new MainWindow(environment.App, await environment.OpenChatAsync());
        try
        {
            window.Show();
            var viewModel = window.ViewModel!;
            viewModel.ShowChatCommand.Execute(null);
            var workbench = viewModel.Workbench;
            var neighborBuilds = 0;
            using var first = workbench.RegisterChatHeaderItem("first", _ => new Button { Content = "first" });
            using var second = workbench.RegisterChatHeaderItem("second", _ =>
            {
                neighborBuilds++;
                return new Button { Content = "second" };
            });
            Render(window);
            var host = Assert.Single(window.GetVisualDescendants().OfType<ItemsControl>(), control => control.Name == "PluginChatHeaderHost");
            var neighbor = FindControlByText<Button>(host, "second");
            Assert.Collection(host.GetVisualDescendants().OfType<Button>(),
                button => Assert.Equal("first", button.Content),
                button => Assert.Same(neighbor, button));

            // Hold the dispatcher until both registration changes can be reconciled together.
            using var reloaded = Task.Run(() =>
            {
                first.Dispose();
                return workbench.RegisterChatHeaderItem("first", _ => new Button { Content = "reloaded" });
            }).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            Render(window);
            Assert.Collection(host.GetVisualDescendants().OfType<Button>(),
                button => Assert.Same(neighbor, button),
                button => Assert.Equal("reloaded", button.Content));
            Assert.Equal(1, neighborBuilds);
        }
        finally
        {
            window.Close();
            await window.ViewModel!.DisposeAsync();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public async Task Workbench_PageReplacementReleasesTheOldTreeOnce_AndRestoresTheBuiltInPage() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var window = new MainWindow(environment.App, await environment.OpenChatAsync());
        try
        {
            window.Show();
            var workspace = window.ViewModel!.Workspace;
            const string pageId = WorkspacePages.Files;
            var defaultPage = workspace.Pages.Available.Single(page => page.Id == pageId);
            var probe = new DisposalProbe();
            var emptyResources = new EmptyPluginResources();
            var themedResources = new ResourceDictionary();
            themedResources.ThemeDictionaries.Add(ThemeVariant.Default, new ResourceDictionary { ["plugin-theme"] = probe });
            var content = new ContentControl { Content = new TextBlock { Text = "plugin content" }, DataContext = probe, Resources = emptyResources };
            var items = new ItemsControl { ItemsSource = new[] { "plugin item" }, DataContext = probe, Resources = themedResources };
            var page = new DisposablePage { Children = { content, items }, DataContext = probe };
            Assert.False(((IResourceNode)content).HasResources);
            Assert.False(((IResourceNode)items).HasResources);

            using var first = window.ViewModel.Workbench.RegisterPage(pageId, "first", _ => new TextBlock { Text = "first page" });
            using var replacement = window.ViewModel.Workbench.RegisterPage(pageId, "replacement", _ => page);
            workspace.OpenPage(workspace.Pages.Available.Single(entry => entry.Id == pageId));
            Render(window);
            Assert.True(page.IsEffectivelyVisible);

            replacement.Dispose();
            Assert.Equal(1, page.Disposals);
            Assert.Equal(1, probe.Disposals);
            Assert.False(page.IsAttachedToVisualTree());
            Assert.Empty(page.Children);
            Assert.Null(content.Content);
            Assert.Null(items.ItemsSource);
            Assert.NotSame(emptyResources, content.Resources);
            Assert.NotSame(themedResources, items.Resources);
            Assert.Null(emptyResources.Owner);
            Assert.Null(themedResources.Owner);
            Assert.Equal("first", workspace.ActivePage?.Label);

            first.Dispose();
            Render(window);
            Assert.Same(defaultPage, workspace.ActivePage);
            Assert.Same(defaultPage, workspace.Pages.Available.Single(entry => entry.Id == pageId));
            Assert.True(Assert.Single(window.GetVisualDescendants().OfType<HostPageView>()).IsEffectivelyVisible);
            Assert.Equal(1, page.Disposals);
        }
        finally
        {
            window.Close();
            await window.ViewModel!.DisposeAsync();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workbench_ReportsFactoryErrorsAndLetsTheCallerRemoveTheRegistration(bool fromWorker)
        => await HeadlessGui.RunAsync(async () =>
        {
            await using var environment = await GuiTestEnvironment.CreateAsync();
            var window = new MainWindow(environment.App, await environment.OpenChatAsync());
            try
            {
                window.Show();
                Render(window);
                var model = window.ViewModel!;
                const string slot = "failing-toolbar-item";
                const string failure = "Toolbar factory failed.";
                const string neighborId = "working-toolbar-item";
                var neighbor = new Button();
                (IDisposable Neighbor, IDisposable Broken) RegisterItems()
                    => (model.Workbench.RegisterToolbarItem(neighborId, _ => neighbor),
                        model.Workbench.RegisterToolbarItem(slot, _ => throw new InvalidOperationException(failure)));
                var registrations = fromWorker
                    ? Task.Run(RegisterItems).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).GetAwaiter().GetResult()
                    : RegisterItems();
                using var working = registrations.Neighbor;
                using var broken = registrations.Broken;

                Render(window);
                Assert.Contains(model.Toasts, toast => toast.IsError && toast.Message == failure);
                Assert.Same(neighbor, Assert.Single(model.Workbench.ToolbarItems).Content);

                broken.Dispose();
                using var replacement = model.Workbench.RegisterToolbarItem(slot, _ => new Button());
                Assert.IsType<Button>(Assert.Single(model.Workbench.ToolbarItems, item => item.Id == slot).Content);
                Assert.Same(neighbor, Assert.Single(model.Workbench.ToolbarItems, item => item.Id == neighborId).Content);
            }
            finally
            {
                window.Close();
                await window.ViewModel!.DisposeAsync();
                Dispatcher.UIThread.RunJobs();
            }
        });

    [Fact]
    public async Task Workbench_BackgroundPageReplacementInstallsItsTemplateBeforePublishingContent() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var window = new MainWindow(environment.App, await environment.OpenChatAsync());
        try
        {
            window.Show();
            var viewModel = window.ViewModel!;
            var workspace = viewModel.Workspace;
            var workbench = viewModel.Workbench;
            const string pageId = "replacement-page";
            using var original = workbench.RegisterPage(pageId, "Original", _ => new TextBlock { Text = "Original page" });
            workspace.OpenPage(workspace.Pages.Available.Single(page => page.Id == pageId));
            Render(window);

            (IDisposable? Template, IDisposable? Page) replacement = default;
            bool? publishedWithTemplate = null;
            void ReplaceDuringTemplateUpdate(object? sender, NotifyCollectionChangedEventArgs change)
            {
                window.DataTemplates.CollectionChanged -= ReplaceDuringTemplateUpdate;
                replacement = Task.Run(() =>
                {
                    var template = workbench.RegisterDataTemplate(new FuncDataTemplate<ReplacementPage>(
                        (model, _) => new TextBlock { Text = model.Text }));
                    var page = workbench.RegisterPage(pageId, "Replacement", _ => new ReplacementPage("Replacement page"));
                    return (template, page);
                }).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            }

            void ObservePageContent(object? sender, PropertyChangedEventArgs change)
            {
                if (change.PropertyName == nameof(WorkspaceViewModel.PageContent) && workspace.PageContent is ReplacementPage replacementPage)
                    publishedWithTemplate ??= window.DataTemplates.Any(template => template.Match(replacementPage));
            }

            window.DataTemplates.CollectionChanged += ReplaceDuringTemplateUpdate;
            workspace.PropertyChanged += ObservePageContent;
            try
            {
                // Add a window template while the worker replaces the active page and its template.
                using var trigger = workbench.RegisterDataTemplate(new FuncDataTemplate<int>(
                    (value, _) => new TextBlock { Text = value.ToString() }));
                Render(window);
                Assert.True(publishedWithTemplate, "The replacement reached the presenter before its data template.");
                var host = window.FindControl<ContentControl>("WorkspacePageHost")!;
                Assert.True(FindControlByText<TextBlock>(host, "Replacement page").IsEffectivelyVisible);
            }
            finally
            {
                window.DataTemplates.CollectionChanged -= ReplaceDuringTemplateUpdate;
                workspace.PropertyChanged -= ObservePageContent;
                replacement.Page?.Dispose();
                replacement.Template?.Dispose();
            }
        }
        finally
        {
            window.Close();
            await window.ViewModel!.DisposeAsync();
            Dispatcher.UIThread.RunJobs();
        }
    });

    private sealed record ReplacementPage(string Text);

    private sealed class EmptyPluginResources : ResourceDictionary;

    private sealed class DisposablePage : StackPanel, IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    private sealed class DisposalProbe : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    [Fact]
    public async Task ManagedGuiPlugin_HotAdd_CustomizesAllPartialSlots_AndInteractsWithLiveWindow() => await HeadlessGui.RunAsync(async () =>
    {
        using var artifacts = new TempArtifactFolder();
        var pluginsDir = Path.Combine(artifacts.Root, "installed-plugins");
        var homeDir = TempTree.CreateSocketDirectory("ga");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(homeDir, "settings.yaml"), GuiTestEnvironment.HomeSettings, TestContext.Current.CancellationToken);
            using var app = await ConfigBoot.Compose(
                new HarnessOptions(HarnessHome.Resolve(homeDir), homeDir, PluginsDirectory: pluginsDir),
                global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
            await using var hostFixture = await StartInProcessHostAsync(app, homeDir);
            var dllPath = CompileGuiPlugin(artifacts.Root, "build-v1", "PartialGuiSample", "@test/gui-partial", "v1-banner");
            var window = new MainWindow(app, hostFixture.Chat);
            window.Show();
            try
            {
                var viewModel = window.ViewModel!;
                Assert.False(viewModel.Workbench.HasToolbarItems);
                Assert.False(viewModel.Workbench.HasChatHeaderItems);
                Assert.False(viewModel.Workbench.HasChatBanners);
                Assert.False(viewModel.Workbench.HasStatusItems);
                Assert.False(viewModel.Workbench.HasSettingsPanels);
                Assert.Equal([WorkspacePages.Files, WorkspacePages.Git, WorkspacePages.Machines], viewModel.Workspace.Pages.Available.Select(page => page.Id));

                await InstallViaMarketAsync(window, dllPath, "@test/gui-partial");
                Render(window);

                Assert.Equal([WorkspacePages.Files, WorkspacePages.Git, WorkspacePages.Machines, "plugin-inspector"], viewModel.Workspace.Pages.Available.Select(page => page.Id));
                Assert.True(viewModel.Workbench.HasToolbarItems);

                var toolbarButton = FindControlByText<Button>(window.FindControl<ItemsControl>("PluginToolbarHost")!, "toolbar:v1-banner");
                ExecuteButton(toolbarButton);
                Render(window);

                Assert.Equal(AppPage.WorkspacePage, viewModel.Page);
                Assert.Equal("plugin-inspector", viewModel.Workspace.ActivePage?.Id);
                var pageHost = window.FindControl<ContentControl>("WorkspacePageHost")!;
                var pageText = FindControlByText<TextBlock>(pageHost, $"page:v1-banner:{MachineSpec.LocalName}");
                Assert.True(pageText.IsEffectivelyVisible);

                viewModel.ShowChatCommand.Execute(null);
                Render(window);
                var chatView = window.GetVisualDescendants().OfType<ChatView>().Single();
                var headerButton = FindControlByText<Button>(chatView.FindControl<ItemsControl>("PluginChatHeaderHost")!, "header:v1-banner");
                ExecuteButton(headerButton);
                Render(window);
                Assert.Equal($"plugin-session:{viewModel.CurrentSessionId.Value}", viewModel.StatusText);

                var bannerText = FindControlByText<TextBlock>(chatView.FindControl<ItemsControl>("PluginChatBannerHost")!, "banner:v1-banner:v1-default");
                Assert.True(bannerText.IsEffectivelyVisible);
                var statusText = FindControlByText<TextBlock>(chatView.FindControl<ItemsControl>("PluginStatusHost")!, "status:v1-banner");
                Assert.True(statusText.IsEffectivelyVisible);

                viewModel.ShowSettingsCommand.Execute(null);
                var pluginsNav = viewModel.Preferences.Sections.Single(section => section.Section == SettingsSection.Plugins);
                pluginsNav.SelectCommand!.Execute(pluginsNav);
                Render(window);
                var settingsView = window.GetVisualDescendants().OfType<SettingsPageView>().Single();
                var panelHost = settingsView.FindControl<ItemsControl>("PluginSettingsPanelsHost")!;
                Assert.True(FindControlByText<TextBlock>(panelHost, "插件检视面板").IsEffectivelyVisible);
                Assert.True(FindControlByText<TextBlock>(panelHost, "settings:v1-banner").IsEffectivelyVisible);

                window.Close();
                Dispatcher.UIThread.RunJobs();
                var reopened = new MainWindow(app, await hostFixture.OpenAdditionalChatAsync());
                reopened.Show();
                try
                {
                    Render(reopened);
                    Assert.True(FindControlByText<Button>(reopened.FindControl<ItemsControl>("PluginToolbarHost")!, "toolbar:v1-banner").IsEffectivelyVisible);
                }
                finally
                {
                    reopened.Close();
                    await reopened.ViewModel!.DisposeAsync();
                    Dispatcher.UIThread.RunJobs();
                }
            }
            finally
            {
                window.Close();
                await window.ViewModel!.DisposeAsync();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            TempTree.Delete(homeDir);
        }
    }, TimeSpan.FromSeconds(60));

    [Fact]
    public async Task ManagedGuiPlugin_HotReload_PreservesParameters_RefreshesForegroundPage_AndCollectsOldLoadContext() => await HeadlessGui.RunAsync(async () =>
    {
        using var artifacts = new TempArtifactFolder();
        var pluginsDir = Path.Combine(artifacts.Root, "installed-plugins");
        var homeDir = TempTree.CreateSocketDirectory("gr");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(homeDir, "settings.yaml"), GuiTestEnvironment.HomeSettings, TestContext.Current.CancellationToken);
            using var app = await ConfigBoot.Compose(
                new HarnessOptions(HarnessHome.Resolve(homeDir), homeDir, PluginsDirectory: pluginsDir),
                global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
            await using var hostFixture = await StartInProcessHostAsync(app, homeDir);
            var window = new MainWindow(app, hostFixture.Chat);
            window.Show();
            try
            {
                var viewModel = window.ViewModel!;
                var v1Path = CompileGuiPlugin(artifacts.Root, "v1", "HotReloadSample", "@test/gui-reload", "v1");
                await InstallViaMarketAsync(window, v1Path, "@test/gui-reload");
                viewModel.Settings.SavePluginParameters("@test/gui-reload", new Dictionary<string, object?> { ["Greeting"] = "kept-greeting" });

                var v1Weak = CaptureLoadContext("HotReloadSample");
                viewModel.Workspace.OpenPage(viewModel.Workspace.Pages.Available.Single(entry => entry.Id == "plugin-inspector"));
                Render(window);
                FocusWorkspacePageButton(window, "focus:v1");
                Render(window);

                var v2Path = CompileGuiPlugin(artifacts.Root, "v2", "HotReloadSample", "@test/gui-reload", "v2");
                var reloadResult = await hostFixture.Chat.RunAsync($"/plugins reload @test/gui-reload {v2Path}");
                Assert.Contains("reloaded", reloadResult, StringComparison.Ordinal);
                Assert.Contains("plugin @test/gui-reload activated", reloadResult, StringComparison.Ordinal);
                Render(window);

                Assert.Equal(AppPage.WorkspacePage, viewModel.Page);
                Assert.Equal("plugin-inspector", viewModel.Workspace.ActivePage?.Id);
                Assert.True(FindControlByText<TextBlock>(window.FindControl<ContentControl>("WorkspacePageHost")!, $"page:v2:{MachineSpec.LocalName}").IsEffectivelyVisible);

                viewModel.ShowChatCommand.Execute(null);
                Render(window);
                var chatView = window.GetVisualDescendants().OfType<ChatView>().Single();
                Assert.True(FindControlByText<TextBlock>(chatView.FindControl<ItemsControl>("PluginChatBannerHost")!, "banner:v2:kept-greeting").IsEffectivelyVisible);

                await Task.Yield();
                Assert.True(PluginUnloader.WaitForCollection(v1Weak, out var v1Report, maxRounds: CollectionRounds), v1Report);

                var v3Path = CompileGuiPlugin(artifacts.Root, "v3", "HotReloadSample", "@test/gui-reload", "v3");
                viewModel.ShowMarketCommand.Execute(null);
                await GuiTestEnvironment.WaitForAsync(viewModel.Preferences.Plugins,
                    () => viewModel.Preferences.Plugins.Any(row => row.Package == "@test/gui-reload"));
                Render(window);
                viewModel.Preferences.PluginInput = v3Path;
                var reloadButton = MarketRowButtons(window, "@test/gui-reload").Single(button => Equals(button.Content, "重载"));
                ExecuteButton(reloadButton);
                await viewModel.Preferences.PluginActionCommand.ExecutionTask!;
                Render(window);

                Assert.Equal("", viewModel.Preferences.PluginInput);
                viewModel.ShowChatCommand.Execute(null);
                Render(window);
                Assert.True(FindControlByText<TextBlock>(chatView.FindControl<ItemsControl>("PluginChatBannerHost")!, "banner:v3:kept-greeting").IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
                await window.ViewModel!.DisposeAsync();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            TempTree.Delete(homeDir);
        }
    }, TimeSpan.FromSeconds(60));

    [Fact]
    public async Task ManagedGuiPlugin_DisableEnableAndRemove_EvictsActivePage_AndCollectsLoadContext() => await HeadlessGui.RunAsync(async () =>
    {
        using var artifacts = new TempArtifactFolder();
        var pluginsDir = Path.Combine(artifacts.Root, "installed-plugins");
        var homeDir = TempTree.CreateSocketDirectory("gd");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(homeDir, "settings.yaml"), GuiTestEnvironment.HomeSettings, TestContext.Current.CancellationToken);
            using var app = await ConfigBoot.Compose(
                new HarnessOptions(HarnessHome.Resolve(homeDir), homeDir, PluginsDirectory: pluginsDir),
                global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
            await using var hostFixture = await StartInProcessHostAsync(app, homeDir);
            var window = new MainWindow(app, hostFixture.Chat);
            window.Show();
            try
            {
                var viewModel = window.ViewModel!;
                var dllPath = CompileGuiPlugin(artifacts.Root, "v1", "LifecycleGuiSample", "@test/gui-lifecycle", "live");
                await InstallViaMarketAsync(window, dllPath, "@test/gui-lifecycle");
                var weak = CaptureLoadContext("LifecycleGuiSample");

                viewModel.Workspace.OpenPage(viewModel.Workspace.Pages.Available.Single(entry => entry.Id == "plugin-inspector"));
                Render(window);
                Assert.Equal(AppPage.WorkspacePage, viewModel.Page);

                await hostFixture.Chat.RunAsync("/plugins disable @test/gui-lifecycle");
                Render(window);
                Assert.Equal(AppPage.Dashboard, viewModel.Page);
                Assert.DoesNotContain(viewModel.Workspace.Pages.Available, entry => entry.Id == "plugin-inspector");
                Assert.False(viewModel.Workbench.HasToolbarItems);
                Assert.False(viewModel.Workbench.HasChatBanners);

                await hostFixture.Chat.RunAsync("/plugins enable @test/gui-lifecycle");
                Render(window);
                Assert.Contains(viewModel.Workspace.Pages.Available, entry => entry.Id == "plugin-inspector");
                Assert.True(viewModel.Workbench.HasToolbarItems);

                viewModel.ShowMarketCommand.Execute(null);
                await GuiTestEnvironment.WaitForAsync(viewModel.Preferences.Plugins,
                    () => viewModel.Preferences.Plugins.Any(row => row.Package == "@test/gui-lifecycle"));
                Render(window);
                var removeButton = MarketRowButtons(window, "@test/gui-lifecycle").Single(button => Equals(button.Content, "移除"));
                ExecuteButton(removeButton);
                await viewModel.Preferences.PluginActionCommand.ExecutionTask!;
                Render(window);

                Assert.False(Directory.Exists(Path.Combine(pluginsDir, "LifecycleGuiSample")));
                Assert.DoesNotContain(viewModel.Workspace.Pages.Available, entry => entry.Id == "plugin-inspector");
                await Task.Yield();
                Assert.True(PluginUnloader.WaitForCollection(weak, out var report, maxRounds: CollectionRounds), report);
            }
            finally
            {
                window.Close();
                await window.ViewModel!.DisposeAsync();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            TempTree.Delete(homeDir);
        }
    }, TimeSpan.FromSeconds(60));

    [Fact]
    public async Task SeparateHostAndGuiContexts_SynchronizeHotAddReloadAndRemoveAcrossManagers() => await HeadlessGui.RunAsync(async () =>
    {
        using var artifacts = new TempArtifactFolder();
        var pluginsDir = Path.Combine(artifacts.Root, "shared-plugins");
        var homeDir = TempTree.CreateSocketDirectory("gs");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(homeDir, "settings.yaml"), GuiTestEnvironment.HomeSettings, TestContext.Current.CancellationToken);
            using var hostApp = await ConfigBoot.Compose(
                new HarnessOptions(HarnessHome.Resolve(homeDir), homeDir, PluginsDirectory: pluginsDir),
                global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
            await using var hostServer = await StartStandaloneHostServerAsync(hostApp, homeDir);

            using var guiApp = await ConfigBoot.Compose(
                new HarnessOptions(HarnessHome.Resolve(homeDir), homeDir, PluginsDirectory: pluginsDir),
                global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
            Assert.Null(guiApp.Ctx.GetProp("hostBackendInProcess"));

            await using var fleet = FleetWorkspace.ForContext(guiApp.Ctx, guiApp.Home, startLocalHost: null);
            using var chat = await HostChatSession.OpenAsync(fleet, homeDir, hostServer.SessionId.Value, TestContext.Current.CancellationToken);
            var window = new MainWindow(guiApp, chat);
            window.Show();
            try
            {
                var viewModel = window.ViewModel!;
                var v1Path = CompileGuiPlugin(artifacts.Root, "v1", "DualProcessSample", "@test/gui-dual", "dual-v1");
                await InstallViaMarketAsync(window, v1Path, "@test/gui-dual");
                Render(window);

                Assert.Equal(ActivationState.Active, hostApp.Composition!.Find("@test/gui-dual")?.State);
                Assert.Equal(ActivationState.Active, guiApp.Composition!.Find("@test/gui-dual")?.State);
                Assert.NotNull(FindControlByText<Button>(window.FindControl<ItemsControl>("PluginToolbarHost")!, "toolbar:dual-v1"));

                var v2Path = CompileGuiPlugin(artifacts.Root, "v2", "DualProcessSample", "@test/gui-dual", "dual-v2");
                viewModel.ShowChatCommand.Execute(null);
                Render(window);
                viewModel.Composer.Input = $"/plugins reload @test/gui-dual {v2Path}";
                await viewModel.SubmitCommand.ExecuteAsync(null);
                Render(window);

                Assert.NotNull(FindControlByText<Button>(window.FindControl<ItemsControl>("PluginToolbarHost")!, "toolbar:dual-v2"));

                viewModel.Composer.Input = "/plugins remove @test/gui-dual";
                await viewModel.SubmitCommand.ExecuteAsync(null);
                Render(window);

                Assert.Null(hostApp.Composition.Find("@test/gui-dual"));
                Assert.Null(guiApp.Composition.Find("@test/gui-dual"));
                Assert.False(viewModel.Workbench.HasToolbarItems);
                Assert.False(Directory.Exists(Path.Combine(pluginsDir, "DualProcessSample")));
            }
            finally
            {
                window.Close();
                await window.ViewModel!.DisposeAsync();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            TempTree.Delete(homeDir);
        }
    }, TimeSpan.FromSeconds(60));

    [Fact(Timeout = 60000)]
    public async Task SandboxEnabled_ExternalGuiPluginWithoutHostRole_IsRejected_UntilExplicitlyTrusted()
    {
        await SandboxTestBackend.RequireAsync();
        using var artifacts = new TempArtifactFolder();
        var pluginsDir = Path.Combine(artifacts.Root, "sandbox-plugins");
        var homeDir = TempTree.CreateSocketDirectory("gx");
        try
        {
            const string sandboxYaml = """
                logging:
                  file: false
                sandbox:
                  enabled: true
                """;
            await File.WriteAllTextAsync(Path.Combine(homeDir, "settings.yaml"), sandboxYaml, TestContext.Current.CancellationToken);
            var dllPath = CompileGuiPlugin(artifacts.Root, "v1", "SandboxGuiSample", "@test/gui-sandbox", "trusted");

            using var app = await ConfigBoot.Compose(
                new HarnessOptions(HarnessHome.Resolve(homeDir), homeDir, PluginsDirectory: pluginsDir),
                global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
            var manager = app.Ctx.Get<HarnessPluginManager>("pluginManager")!;

            var denied = await Assert.ThrowsAsync<RuntimeException>(() => manager.AddAsync(dllPath, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("PLUGIN_HOST_ROLE_REQUIRED", denied.Code);
            Assert.Null(app.Composition!.Find("@test/gui-sandbox"));

            const string trustedYaml = """
                logging:
                  file: false
                sandbox:
                  enabled: true
                plugins:
                  "@test/gui-sandbox":
                    enabled: true
                    execution: host
                """;
            await File.WriteAllTextAsync(Path.Combine(homeDir, "settings.yaml"), trustedYaml, TestContext.Current.CancellationToken);
            var added = await manager.AddAsync(dllPath, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("activated", added, StringComparison.Ordinal);
            Assert.Equal(ActivationState.Active, app.Composition.Find("@test/gui-sandbox")?.State);
            Assert.True(app.Ctx.Get<GuiWorkbench>(GuiWorkbench.ServiceName)!.ToolbarItems.Count > 0 || app.Composition.Find("@test/gui-sandbox")?.IsActive == true);
            await manager.RemoveAsync("@test/gui-sandbox");
        }
        finally
        {
            TempTree.Delete(homeDir);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FocusWorkspacePageButton(Window window, string text)
    {
        var button = FindControlByText<Button>(window.FindControl<ContentControl>("WorkspacePageHost")!, text);
        Assert.True(button.Focus());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureLoadContext(string assemblyName)
    {
        var context = AssemblyLoadContext.All.Single(candidate => candidate.Name == $"dsh-plugin:{assemblyName}");
        return new WeakReference(context, trackResurrection: true);
    }

    private static async Task InstallViaMarketAsync(MainWindow window, string dllPath, string package)
    {
        var viewModel = window.ViewModel!;
        viewModel.ShowMarketCommand.Execute(null);
        await GuiTestEnvironment.WaitForAsync(viewModel.Preferences.Plugins, () => viewModel.Preferences.Plugins.Count > 0);
        Render(window);
        viewModel.Preferences.PluginInput = dllPath;
        await viewModel.Preferences.AddPluginCommand.ExecuteAsync(null);
        await GuiTestEnvironment.WaitForAsync(viewModel.Preferences.Plugins,
            () => viewModel.Preferences.Plugins.Any(row => row.Package == package && row.Enabled));
    }

    private static IReadOnlyList<Button> MarketRowButtons(Window window, string package)
        => [.. window.GetVisualDescendants().OfType<MarketPageView>().Single().GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.DataContext is PluginRowViewModel row && row.Package == package)];

    private static T FindControlByText<T>(Control root, string text) where T : Control
    {
        if (typeof(T) == typeof(TextBlock))
            return (T)(Control)root.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == text);
        return root.GetVisualDescendants().OfType<T>().Single(control =>
            (control is Button button && Equals(button.Content, text))
            || control.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == text));
    }

    private static void ExecuteButton(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        if (button.Command is not null)
            button.Command.Execute(button.CommandParameter);
        else
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Render(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    private static string CompileGuiPlugin(string root, string folder, string assemblyName, string package, string tag)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, folder)).FullName;
        var dllPath = Path.Combine(directory, $"{assemblyName}.dll");
        var source = $$"""
            using System;
            using Avalonia.Controls;
            using Avalonia.Controls.Templates;
            using Dsh.Gui;
            using Dsh.Gui.ViewModels;
            using Dsh.Plugins;
            using Dsh.Runtime;

            [assembly: DshPlugin("{{package}}")]

            namespace SampleGuiPlugin;

            public sealed record SampleConfig
            {
                public string Greeting { get; init; } = "v1-default";
            }

            public sealed record InspectorPageModel(string Text, string FocusLabel);

            public sealed class InspectorDataTemplate : IDataTemplate
            {
                public Control? Build(object? param)
                {
                    if (param is not InspectorPageModel model)
                        return null;
                    var panel = new StackPanel();
                    panel.Children.Add(new TextBlock { Text = model.Text });
                    panel.Children.Add(new Button { Content = model.FocusLabel });
                    panel.Children.Add(new ContentControl
                    {
                        Content = new ItemsControl { ItemsSource = new[] { new SampleConfig() } }
                    });
                    return panel;
                }

                public bool Match(object? data) => data is InspectorPageModel;
            }

            public sealed class Plugin : IDshPlugin
            {
                public string[] Inject => [];

                public Type[] InjectTypes => [typeof(GuiWorkbench)];

                public Type ConfigType => typeof(SampleConfig);

                public IDisposable Apply(Context ctx, object? config)
                {
                    var cfg = config as SampleConfig ?? new SampleConfig();
                    var workbench = ctx.Get<GuiWorkbench>()!;
                    IDisposable[] handles =
                    [
                        workbench.RegisterDataTemplate(new InspectorDataTemplate()),
                        workbench.RegisterPage("plugin-inspector", "插件检视", pageCtx =>
                            new InspectorPageModel($"page:{{tag}}:{pageCtx.Machine}", "focus:{{tag}}")),
                        workbench.RegisterToolbarItem("plugin-toolbar", shell =>
                        {
                            var button = new Button { Content = "toolbar:{{tag}}" };
                            button.Click += (_, _) => shell.OpenPage("plugin-inspector");
                            return button;
                        }),
                        workbench.RegisterChatHeaderItem("plugin-header", session =>
                        {
                            var button = new Button { Content = "header:{{tag}}" };
                            button.Click += (_, _) => session.SetStatus($"plugin-session:{session.SessionId}");
                            return button;
                        }),
                        workbench.RegisterChatBanner("plugin-banner", _ =>
                            new TextBlock { Text = $"banner:{{tag}}:{cfg.Greeting}" }),
                        workbench.RegisterStatusItem("plugin-status", _ =>
                            new TextBlock { Text = "status:{{tag}}" }),
                        workbench.RegisterSettingsPanel("plugin-settings", "插件检视面板", _ =>
                            new TextBlock { Text = "settings:{{tag}}" }),
                    ];
                    return new Scope(handles);
                }

                private sealed class Scope(IDisposable[] handles) : IDisposable
                {
                    public void Dispose()
                    {
                        for (var index = handles.Length - 1; index >= 0; index--)
                            handles[index].Dispose();
                    }
                }
            }
            """;

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            PluginReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver.Create(new DshPluginCatalogGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var generatorDiagnostics);
        Assert.DoesNotContain(generatorDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var emit = generated.Emit(dllPath);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return dllPath;
    }

    private static MetadataReference[] PluginReferences()
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        string[] extra =
        [
            typeof(IDshPlugin).Assembly.Location,
            typeof(Context).Assembly.Location,
            typeof(GuiWorkbench).Assembly.Location,
            typeof(Control).Assembly.Location,
            typeof(Avalonia.AvaloniaObject).Assembly.Location,
        ];
        return [.. trusted.Concat(extra).Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.Ordinal).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    }

    private static async Task<InProcessHostLease> StartInProcessHostAsync(HarnessApp app, string homeDir)
    {
        var agents = app.Ctx.Get<Dsh.Agent.AgentRegistry>(Dsh.Agent.AgentRegistry.ServiceName)!;
        var registration = ProviderRegistrar.RegisterProvider(app.Ctx, new HarnessOptions(app.Home), "test",
            new ProviderSettings
            {
                Type = ProviderTypes.OpenAiCompatible,
                Options = new ProviderOptions { BaseUrl = "http://127.0.0.1:1/v1", ApiKey = "test" },
                Models = new() { ["test-model"] = new ProviderModelSettings() },
            }, isDefault: false);
        var handle = await agents.Create(new Dsh.Agent.CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            homeDir,
            new AgentOptions("test", "test-model")));
        await handle.Agent.WhenIdle();
        var store = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        foreach (var session in store.List())
            await store.Flush(session);
        var backend = await HarnessRemoteHostBackend.CreateAsync(app, HostPtyDaemonForTests.Unused, TestContext.Current.CancellationToken);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var listener = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(
            new RemoteHostServer(new RemoteHostServerOptions(null), backend), app.Home.Root, cts.Token));
        var fleet = FleetWorkspace.ForContext(app.Ctx, app.Home, startLocalHost: null);
        var chat = await HostChatSession.OpenAsync(fleet, homeDir, handle.Agent.Id.Value, TestContext.Current.CancellationToken);
        return new InProcessHostLease(app, homeDir, handle.Agent.Id.Value, registration.Handle!, handle.Dispose, backend, cts, listener, fleet, chat);
    }

    private static async Task<StandaloneHostServer> StartStandaloneHostServerAsync(HarnessApp hostApp, string homeDir)
    {
        var agents = hostApp.Ctx.Get<Dsh.Agent.AgentRegistry>(Dsh.Agent.AgentRegistry.ServiceName)!;
        var registration = ProviderRegistrar.RegisterProvider(hostApp.Ctx, new HarnessOptions(hostApp.Home), "test",
            new ProviderSettings
            {
                Type = ProviderTypes.OpenAiCompatible,
                Options = new ProviderOptions { BaseUrl = "http://127.0.0.1:1/v1", ApiKey = "test" },
                Models = new() { ["test-model"] = new ProviderModelSettings() },
            }, isDefault: false);
        var handle = await agents.Create(new Dsh.Agent.CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            homeDir,
            new AgentOptions("test", "test-model")));
        await handle.Agent.WhenIdle();
        var store = hostApp.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        foreach (var session in store.List())
            await store.Flush(session);
        var backend = await HarnessRemoteHostBackend.CreateAsync(hostApp, HostPtyDaemonForTests.Unused, TestContext.Current.CancellationToken);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var listener = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(
            new RemoteHostServer(new RemoteHostServerOptions(null), backend), hostApp.Home.Root, cts.Token));
        return new StandaloneHostServer(handle.Agent.Id, registration.Handle!, handle.Dispose, backend, cts, listener);
    }

    private sealed class InProcessHostLease(
        HarnessApp app,
        string homeDir,
        string sessionId,
        IDisposable provider,
        IDisposable agentHandle,
        HarnessRemoteHostBackend backend,
        CancellationTokenSource cts,
        Task listener,
        FleetWorkspace fleet,
        HostChatSession chat) : IAsyncDisposable
    {
        private readonly List<(FleetWorkspace Fleet, HostChatSession Chat)> _extra = [];

        public HostChatSession Chat { get; } = chat;

        public async Task<HostChatSession> OpenAdditionalChatAsync()
        {
            var extraFleet = FleetWorkspace.ForContext(app.Ctx, app.Home, startLocalHost: null);
            var extraChat = await HostChatSession.OpenAsync(extraFleet, homeDir, sessionId, TestContext.Current.CancellationToken);
            _extra.Add((extraFleet, extraChat));
            return extraChat;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var (extraFleet, extraChat) in _extra)
            {
                extraChat.Dispose();
                await extraFleet.DisposeAsync();
            }
            Chat.Dispose();
            await fleet.DisposeAsync();
            await cts.CancelAsync();
            try { await listener; }
            catch (OperationCanceledException) { }
            cts.Dispose();
            await backend.DisposeAsync();
            agentHandle.Dispose();
            provider.Dispose();
        }
    }

    private sealed class StandaloneHostServer(
        SessionId sessionId,
        IDisposable provider,
        IDisposable agentHandle,
        HarnessRemoteHostBackend backend,
        CancellationTokenSource cts,
        Task listener) : IAsyncDisposable
    {
        public SessionId SessionId { get; } = sessionId;

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            try { await listener; }
            catch (OperationCanceledException) { }
            cts.Dispose();
            await backend.DisposeAsync();
            agentHandle.Dispose();
            provider.Dispose();
        }
    }

    private sealed class TempArtifactFolder : IDisposable
    {
        public string Root { get; } = TempTree.CreateDirectory("plugin-gui-artifacts");
        public void Dispose() => TempTree.Delete(Root);
    }
}
