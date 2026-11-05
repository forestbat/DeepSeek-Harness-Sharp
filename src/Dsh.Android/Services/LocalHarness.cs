using Dsh.Boot;
using Dsh.Host;
using Dsh.RemoteHost;

namespace Dsh.Android.Services;

/**
 * 手机自己的 host: 在 App 进程内组合一份 harness, 把手机能力注册成原生工具,
 * 再用一对内存流接上标准 host 协议。这样"本机"会话与远端会话走同一套语义, agent 真正跑在手机上。
 */
public sealed class LocalHarness(LocalAgentStore store) : IMachineLink
{
    public const string MachineName = "本机";
    private const string ClientName = "dsh-phone";

    private readonly HostProjection _projection = new();
    private readonly CancellationTokenSource _lifetime = new();
    private HarnessApp? _app;
    private IDisposable? _tools;
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
            var settings = store.Load();
            _app = await ConfigBoot.Compose(new HarnessOptions(
                home,
                files,
                settings.Provider,
                settings.Model,
                settings.BaseUrl is { Length: > 0 } ? settings.BaseUrl : null,
                ApiKey: settings.ApiKey,
                IsTui: false));
            _tools = PhoneTools.Register(_app.Ctx, new AndroidPhoneDevice());
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
        if (_backend is not null)
            await _backend.DisposeAsync();
        _app?.Dispose();
        _lifetime.Dispose();
    }

    private void SetState(MobileConnectionState state, string? detail)
    {
        State = state;
        Detail = detail;
        Changed?.Invoke();
    }
}
