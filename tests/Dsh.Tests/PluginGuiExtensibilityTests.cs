using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
