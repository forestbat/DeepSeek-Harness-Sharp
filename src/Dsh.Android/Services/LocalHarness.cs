using Dsh.Boot;
using Dsh.Gui.Shared.Mobile;
using Dsh.Host;
using Dsh.RemoteHost;
using Dsh.Workspace;

namespace Dsh.Android.Services;

/**
 * 手机自己的 host: 在 App 进程内组合一份 harness, 把手机能力注册成原生工具,
 * 再用一对内存流接上标准 host 协议。这样"本机"会话与远端会话走同一套语义, agent 真正跑在手机上。
 */
public sealed class LocalHarness(MachineStore machines, LocalAgentStore store, Func<IReadOnlyList<IAgentMachineLink>> links) : IMachineLink
{
    public const string MachineName = "本机";
    private const string ClientName = "dsh-phone";
    private const string SettingsTemplateAsset = "Profiles/Templates/settings.yaml";

    /** 手机自己的 A2A 服务端口; 桌面机按同一个约定端口开放服务端。 */
    private const int A2aPort = 47324;
    private const int RemoteA2aPort = 41443;

    private readonly HostProjection _projection = new();
    private readonly CancellationTokenSource _lifetime = new();
    private HarnessApp? _app;
    private IDisposable? _tools;
    private IDisposable? _machineTools;
    private HarnessRemoteHostBackend? _backend;
    private RemoteHostServer? _server;
    private RemoteHostClient? _client;
    private Task? _serve;
    private Task[] _pumps = [];

    public string Name => MachineName;

    public string Target => "Android 内嵌 host";

    public MobileConnectionState State { get; private set; } = MobileConnectionState.Offline;

    public string? Detail { get; private set; }

    public HostInfo? Info { get; private set; }

    public IReadOnlyList<RemoteSessionSummary> Sessions => _projection.Sessions;

    public RemotePendingList Pending => _projection.Pending;

    public event Action? Changed;

    public bool Running => State == MobileConnectionState.Connected;

    public void Start() => _ = StartAsync();

    public Task<HostLease> WaitConnectedAsync(CancellationToken cancellationToken)
        => State == MobileConnectionState.Connected && _client is { } client
            ? Task.FromResult(new HostLease(client, _lifetime.Token, Task.Delay(Timeout.Infinite, _lifetime.Token)))
            : throw new InvalidOperationException($"本机 host 未启动: {Detail}");

    public IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
        => (_client ?? throw new InvalidOperationException("本机 host 未启动"))
            .SubscribeAsync(sessionId, fromSeq, cancellationToken);

    private async Task StartAsync()
    {
        if (State != MobileConnectionState.Offline)
            return;
        SetState(MobileConnectionState.Connecting, null);
        try
        {
            var files = global::Android.App.Application.Context?.FilesDir?.AbsolutePath
                ?? throw new InvalidOperationException("应用私有目录不可用");
            var home = new HarnessHome(Path.Combine(files, "harness"));
            home.Ensure();
            await EnsureSettingsTemplateAsync(home);
            ConfigureFleet(home);
            ConfigureA2a(home);
            var settings = store.Load();
            _app = await ConfigBoot.Compose(new HarnessOptions(
                home,
                files,
                settings.Provider,
                settings.Model,
                settings.BaseUrl is { Length: > 0 } ? settings.BaseUrl : null,
                ApiKey: settings.ApiKey,
                IsTui: false));
            _tools = PhoneAgentTools.Register(_app.Ctx, new AndroidPhoneDevice(), new PhoneUiAutomation());
            _machineTools = MachineSessionTools.Register(_app.Ctx, links);
            var (serverSide, clientSide) = LocalDuplex.Create();
            _backend = await HarnessRemoteHostBackend.CreateAsync(_app);
            _server = new RemoteHostServer(new RemoteHostServerOptions(), _backend);
            _serve = _server.ServeAsync(serverSide, _lifetime.Token);
            _client = await RemoteHostClient.ConnectAsync(clientSide, null, ClientName, _lifetime.Token);
            Info = await _client.InfoAsync(_lifetime.Token);
            _pumps = _projection.PumpAsync(_client, () => Changed?.Invoke(), _lifetime.Token);
            SetState(MobileConnectionState.Connected, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Detail = error.Message;
            SetState(MobileConnectionState.Offline, error.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try
        {
            await Task.WhenAll(_pumps.Append(_serve ?? Task.CompletedTask)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        SetState(MobileConnectionState.Offline, null);
        if (_client is not null)
            await _client.DisposeAsync();
        _tools?.Dispose();
        _machineTools?.Dispose();
        if (_backend is not null)
            await _backend.DisposeAsync();
        _app?.Dispose();
        _lifetime.Dispose();
    }

    /** 把 App 里配好的桌面机写进本机 host 的 fleet 段: 手机会话才能主动找到并驱动那些机器。 */
    private void ConfigureFleet(HarnessHome home)
    {
        var remote = RemoteMachines();
        var keyPath = remote.Count > 0 ? WriteSshKey(home, machines.LoadPrivateKey()) : null;
        var specs = remote
            .Select(profile => new MachineSpec(profile.Name, "ssh", profile.Host, profile.Port, profile.SshUser, SshAuth.Key, keyPath))
            .ToList();
        new Dsh.Fleet.MachineStore(home).Save(new FleetConfig(specs, new DeploySettings()));
    }

    /**
     * A2A 这条纯 HTTP 的路在手机上同样可用: 本机只认回环(对外开端口要配令牌, 那是另一档事),
     * 桌面机按约定端口当远端, 令牌沿用 App 里给这台机器填的访问令牌。
     */
    private void ConfigureA2a(HarnessHome home)
    {
        var settings = HarnessSettings.Load(home);
        var a2a = settings.A2a ?? new A2aSettings();
        a2a.Enabled = true;
        a2a.Host = "127.0.0.1";
        a2a.Port = A2aPort;
        foreach (var profile in RemoteMachines())
            a2a.Remotes[profile.Name] = new A2aRemoteSettings
            {
                Url = $"http://{profile.Host}:{RemoteA2aPort}/",
                Token = profile.Token.Length > 0 ? profile.Token : null,
            };
        settings.A2a = a2a;
        settings.Save(home);
    }

    private IReadOnlyList<HostProfile> RemoteMachines()
        => [.. machines.Load().Where(profile => profile.Transport == HostTransportKind.Ssh && profile.Host.Length > 0)];

    private static string? WriteSshKey(HarnessHome home, string? pem)
    {
        if (pem is not { Length: > 0 })
            return null;
        var path = Path.Combine(home.Root, "ssh", "id_rsa");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, pem);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    /**
     * 模板在 APK 里只是资源; 首次启动要落成普通文件。
     * 会话目录那份给 Compose 读, 程序集目录那份给直接问模板的插件(如由 settings 生成界面的 ide)。
     */
    private static async Task EnsureSettingsTemplateAsync(HarnessHome home)
    {
        var context = global::Android.App.Application.Context
            ?? throw new InvalidOperationException("应用上下文不可用");
        var targets = new List<string>();
        if (!File.Exists(home.SettingsFile))
            targets.Add(home.SettingsFile);
        var beside = Path.Combine(AppContext.BaseDirectory, SettingsTemplateAsset);
        if (!File.Exists(beside))
            targets.Add(beside);
        if (targets.Count == 0)
            return;
        await using var asset = context.Assets?.Open(SettingsTemplateAsset)
            ?? throw new InvalidOperationException("安装包缺少 settings.yaml 模板");
        using var reader = new StreamReader(asset);
        var template = await reader.ReadToEndAsync();
        foreach (var target in targets)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, template);
            }
            catch (Exception error) when (target != home.SettingsFile && error is IOException or UnauthorizedAccessException)
            {
                // 程序集目录只读时忽略: 那是锦上添花, 缺了只有个别插件读默认值失败。
            }
        }
    }

    private void SetState(MobileConnectionState state, string? detail)
    {
        State = state;
        Detail = detail;
        Changed?.Invoke();
    }
}
