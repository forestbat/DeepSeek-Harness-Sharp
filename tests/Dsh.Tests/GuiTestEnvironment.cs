using Dsh.Boot;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Interaction;
using Dsh.Host;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** GUI 组件测试的最小真实环境: 真实插件组合 + 真实持久化, 不启动 UI。 */
public sealed class GuiTestEnvironment : IAsyncDisposable
{

    private sealed class SharedApp
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public HarnessApp? App { get; set; }
        public string? Home { get; set; }
        public string? Settings { get; set; }
        public IReadOnlyList<(string Path, byte[] Content)> Presets { get; set; } = [];
        public HarnessRemoteHostBackend? Backend { get; set; }
        public CancellationTokenSource? Serving { get; set; }
        public Task? Listener { get; set; }
        public int InUse;

        public async Task EnsureHostAsync(HarnessApp app)
        {
            if (Backend is not null)
                return;
            Backend = await HarnessRemoteHostBackend.CreateAsync(app, HostPtyDaemonForTests.Unused, CancellationToken.None);
            Serving = new CancellationTokenSource();
            Listener = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(
                new RemoteHostServer(new RemoteHostServerOptions(null), Backend), app.Home.Root, Serving.Token));
        }
    }

    private static readonly ConcurrentDictionary<string, SharedApp> SharedApps = new(StringComparer.Ordinal);
    private static readonly List<string> SharedOrder = [];
    private static readonly SemaphoreSlim SharedOrderGate = new(1, 1);
    private const int MaxSharedApps = 2;

    private readonly string _directory;
    private readonly SharedApp? _shared;
    private readonly bool _sharedLease;
    private readonly IDisposable? _agentHandle;
    private bool _ownsDirectory = true;
    private readonly List<HostChatSession> _chats = [];
    private HarnessRemoteHostBackend? _backend;
    private FleetWorkspace? _fleet;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listener;
    private IDisposable? _testProvider;

    private GuiTestEnvironment(string directory, HarnessApp app, AgentLoopAgent agent, SharedApp? shared = null, IDisposable? agentHandle = null, bool sharedLease = false)
    {
        _directory = directory;
        _shared = shared;
        _sharedLease = sharedLease;
        _agentHandle = agentHandle;
        App = app;
        Agent = agent;
    }

    public HarnessApp App { get; }

    public AgentLoopAgent Agent { get; }

    /**
     * Compose plugins off the shared headless UI thread so their async setup cannot capture its dispatcher.
     * Async callers use CreateAsync; CreateBlocking is retained for synchronous UI setup.
     */
    public static GuiTestEnvironment CreateBlocking()
        => Task.Run(() => CreateAsync()).GetAwaiter().GetResult();

    public static async Task<GuiTestEnvironment> CreateAsync()
        => await CreateAsync(HomeSettings);

    public static Task<GuiTestEnvironment> CreateIsolatedAsync() => CreateFreshAsync(HomeSettings);
    public static Task<GuiTestEnvironment> CreateIsolatedAsync(string homeSettings) => CreateFreshAsync(homeSettings);

    public static async Task<GuiTestEnvironment> CreateAsync(string homeSettings)
    {
        var key = TestContext.Current.TestCase?.TestClassName;
        if (key is null)
            return await CreateFreshAsync(homeSettings);
        var shared = await SharedAsync($"{key}|{homeSettings}");
        try
        {
            if (shared.App is null)
            {
                var directory = TempTree.CreateSocketDirectory("g");
                File.WriteAllText(Path.Combine(directory, "settings.yaml"), homeSettings);
                shared.App = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(directory), directory));
                shared.Home = directory;
                shared.Settings = homeSettings;
                shared.Presets = Snapshot(Path.Combine(directory, ".agent-presets"));
            }
            else
            {
                await ResetAsync(shared.App, shared);
            }
            var (agent, handle) = await CreateAgentAsync(shared.App, shared.Home!);
            return new GuiTestEnvironment(shared.Home!, shared.App, agent, shared, handle.Dispose, sharedLease: true);
        }
        catch
        {
            Interlocked.Decrement(ref shared.InUse);
            shared.Gate.Release();
            throw;
        }
    }

    private static async Task<GuiTestEnvironment> CreateFreshAsync(string homeSettings)
    {
        var directory = TempTree.CreateSocketDirectory("g");
        File.WriteAllText(Path.Combine(directory, "settings.yaml"), homeSettings);
        var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(directory), directory));
        var (agent, handle) = await CreateAgentAsync(app, directory);
        return new GuiTestEnvironment(directory, app, agent, agentHandle: handle.Dispose);
    }

    private static async Task<SharedApp> SharedAsync(string key)
    {
        await SharedOrderGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var shared = SharedApps.GetOrAdd(key, _ => new SharedApp());
            if (!SharedOrder.Contains(key))
                SharedOrder.Add(key);
            while (SharedOrder.Count > MaxSharedApps)
            {
                var oldest = SharedOrder.FirstOrDefault(candidate =>
                    candidate != key
                    && SharedApps.TryGetValue(candidate, out var candidateApp)
                    && Volatile.Read(ref candidateApp.InUse) == 0);
                if (oldest is null)
                    break;
                SharedOrder.Remove(oldest);
                if (SharedApps.TryRemove(oldest, out var evicted))
                    await DisposeSharedAsync(evicted);
            }
            await shared.Gate.WaitAsync(TestContext.Current.CancellationToken);
            Interlocked.Increment(ref shared.InUse);
            return shared;
        }
        finally
        {
            SharedOrderGate.Release();
        }
    }

    private static async Task DisposeSharedAsync(SharedApp shared)
    {
        await shared.Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (shared.Serving is not null)
            {
                await shared.Serving.CancelAsync();
                try { await shared.Listener!.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                shared.Serving.Dispose();
            }
            if (shared.Backend is not null)
                await shared.Backend.DisposeAsync();
            shared.App?.Dispose();
            if (shared.Home is not null)
                TempTree.Delete(shared.Home);
        }
        finally
        {
            shared.Gate.Release();
        }
    }

    /** 模拟"重启应用": 旧实例关闭, 用同一个 home 重新组合, 并新建一个会话(旧会话不再 live)。 */
    public async Task<GuiTestEnvironment> RestartAsync() => await Task.Run(async () =>
    {
        if (_shared is not null)
            throw new InvalidOperationException("RestartAsync requires an isolated GUI environment.");
        await StopHostAsync();
        _agentHandle?.Dispose();
        if (Agent is IDisposable disposable)
            disposable.Dispose();
        App.Dispose();
        _ownsDirectory = false;
        var homePath = _directory;
        var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(homePath), _directory));
        var (agent, handle) = await CreateAgentAsync(app, _directory);
        return new GuiTestEnvironment(_directory, app, agent, agentHandle: handle.Dispose);
    });

    /** 关掉文件日志:它的句柄释放时机不受测试控制,会让临时目录清理偶发失败。 */
    public const string HomeSettings = """
        logging:
          file: false
        """;

    private static async Task<(AgentLoopAgent Agent, AgentHandle Handle)> CreateAgentAsync(HarnessApp app, string cwd)
    {
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            new AgentOptions("test", "test-model")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        return (agent, handle);
    }

    private static async Task ResetAsync(HarnessApp app, SharedApp shared)
    {
        var registry = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var store = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        foreach (var agent in registry.List())
        {
            if (agent is IDisposable disposable)
                disposable.Dispose();
            registry.Remove(agent.Id);
            store.Release(agent.Id);
        }
        foreach (var session in store.List())
            store.Release(session.Id);
        if (shared.Backend is { } backend)
            foreach (var session in backend.Sessions.List())
                backend.Sessions.Remove(session.SessionId);
        if (app.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false) is { } persistence)
            foreach (var snapshot in persistence.List())
                persistence.Delete(snapshot.Header.Id);
        foreach (var path in new[] { app.Home.SessionsPath })
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        File.WriteAllText(app.Home.SettingsFile, shared.Settings!);
        Restore(Path.Combine(app.Home.Root, ".agent-presets"), shared.Presets);
        await Task.CompletedTask;
    }

    private static IReadOnlyList<(string Path, byte[] Content)> Snapshot(string directory)
        => Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => (Path.GetRelativePath(directory, path), File.ReadAllBytes(path)))]
            : [];

    private static void Restore(string directory, IReadOnlyList<(string Path, byte[] Content)> files)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        foreach (var (path, content) in files)
        {
            var target = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }
    }

    /** Every GUI uses the production Host backend and Fleet client over the authenticated local endpoint. */
    public async Task<HostChatSession> OpenChatAsync(AgentLoopAgent? selected = null)
        => await OpenChatAsync((selected ?? Agent).Id);

    public async Task<HostChatSession> OpenChatAsync(SessionId sessionId)
    {
        var store = App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        foreach (var session in store.List())
            await store.Flush(session);
        if (!App.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!.ListProviders().Any(provider => provider.Id == "test"))
        {
            var registration = ProviderRegistrar.RegisterProvider(App.Ctx, new HarnessOptions(App.Home), "test",
                new ProviderSettings
                {
                    Type = ProviderTypes.OpenAiCompatible,
                    Options = new ProviderOptions { BaseUrl = "http://127.0.0.1:1/v1", ApiKey = "test" },
                    Models = new() { ["test-model"] = new ProviderModelSettings() },
                }, isDefault: false);
            _testProvider = registration.Handle ?? throw new InvalidOperationException(registration.Error);
        }
        if (_shared is not null)
        {
            await _shared.EnsureHostAsync(App);
        }
        else if (_backend is null)
        {
            _backend = await HarnessRemoteHostBackend.CreateAsync(App, HostPtyDaemonForTests.Unused, TestContext.Current.CancellationToken);
            _listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            _listener = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(
                new RemoteHostServer(new RemoteHostServerOptions(null), _backend), App.Home.Root, _listenerCancellation.Token));
        }
        if (_fleet is null)
            _fleet = FleetWorkspace.ForContext(App.Ctx, App.Home, startLocalHost: null);
        var chat = await HostChatSession.OpenAsync(_fleet!, _directory, sessionId.Value, TestContext.Current.CancellationToken);
        _chats.Add(chat);
        var machine = _fleet!.Find(MachineSpec.LocalName)!;
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void SessionsChanged(IWorkspaceMachine connection)
        {
            if (connection.Sessions.Any(session => session.SessionId == chat.Id.Value))
                registered.TrySetResult();
        }
        machine.SessionsChanged += SessionsChanged;
        try
        {
            SessionsChanged(machine);
            await registered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally { machine.SessionsChanged -= SessionsChanged; }
        return chat;
    }

    public static async Task WaitForAsync(FleetWorkspace source, Func<bool> condition)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed()
        {
            if (condition())
                completion.TrySetResult();
        }
        source.Changed += Changed;
        try
        {
            if (!condition())
                await PumpAsync(completion.Task);
        }
        finally { source.Changed -= Changed; }
    }

    public static async Task WaitForAsync(INotifyPropertyChanged source, Func<bool> condition)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (condition())
                completion.TrySetResult();
        }
        source.PropertyChanged += Changed;
        try
        {
            if (!condition())
                await PumpAsync(completion.Task);
        }
        finally { source.PropertyChanged -= Changed; }
    }

    public static Task WaitForAsync<T>(ObservableCollection<T> source, Func<bool> condition, Func<string>? diagnostics = null)
        => WaitForAsync((INotifyCollectionChanged)source, condition, diagnostics);

    public static async Task WaitForAsync(INotifyCollectionChanged source, Func<bool> condition, Func<string>? diagnostics = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (condition())
                completion.TrySetResult();
        }
        source.CollectionChanged += Changed;
        try
        {
            if (!condition())
                await PumpAsync(completion.Task);
        }
        catch (TimeoutException error) when (diagnostics is not null)
        {
            throw new TimeoutException($"Collection condition was not reached. {diagnostics()}", error);
        }
        finally { source.CollectionChanged -= Changed; }
    }

    public static string DescribeChatState(MainViewModel viewModel)
        => $"Status: {viewModel.StatusText}; busy: {viewModel.IsBusy}; message kinds: [{string.Join(", ", viewModel.Messages.Select(message => message.Kind))}]; system messages: [{string.Join(" | ", viewModel.Messages.Where(message => message.Kind == MessageKind.System).Select(message => message.Text))}]";

    /** HeadlessGui keeps the dispatcher running while the real RPC/event completion is awaited. */
    public static Task PumpAsync(Task task)
        => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    public static async Task<T> PumpAsync<T>(Task<T> task)
    {
        await PumpAsync((Task)task);
        return await task;
    }

    private async Task StopHostAsync()
    {
        foreach (var chat in _chats)
            chat.Dispose();
        _chats.Clear();
        if (_fleet is not null)
            await _fleet.DisposeAsync().ConfigureAwait(false);
        if (_listenerCancellation is not null)
        {
            await _listenerCancellation.CancelAsync().ConfigureAwait(false);
            try { await _listener!.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _listenerCancellation.Dispose();
        }
        if (_backend is not null)
            await _backend.DisposeAsync().ConfigureAwait(false);
        _testProvider?.Dispose();
        _testProvider = null;
        _fleet = null;
        _backend = null;
        _listenerCancellation = null;
        _listener = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopHostAsync();
        _agentHandle?.Dispose();
        if (Agent is IDisposable disposable)
            disposable.Dispose();
        if (_shared is not null)
        {
            try
            {
                await ResetAsync(App, _shared);
            }
            finally
            {
                if (_sharedLease)
                {
                    Interlocked.Decrement(ref _shared.InUse);
                    _shared.Gate.Release();
                }
            }
            return;
        }
        App.Dispose();
        if (!_ownsDirectory)
            return;
        TempTree.Delete(_directory);
    }
}
