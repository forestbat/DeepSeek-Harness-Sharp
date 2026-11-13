using Dsh.Boot;
using Dsh.Plugins;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Core;

namespace Dsh.Account;

/**
 * 账号服务: 状态快照 + 发起/取消浏览器登录 + 资料/余额 + 退出。
 * 登录交互全部发生在平台页面, 本服务只做 PKCE 流程与凭据落盘。
 */
public sealed class AccountService : Service, IAccountService, IInferenceCredentials, IDisposable
{
    public const string ServiceName = IAccountService.ServiceName;

    private const string LogName = "deepseek-account";

    private const string LoginSource = "desktop";

    private readonly AccountConfig _config;
    private readonly AccountStore _store;
    private readonly PlatformClient _client;
    private readonly Logger _logger;
    private readonly Func<string, bool> _openBrowser;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _attempt;
    private TaskCompletionSource<AccountProfile?>? _attemptCompletion;
    private string? _authorizeId;
    private string? _authorizeUrl;
    private string? _verifier;
    private AccountProfile? _profile;
    private AccountBalance? _balance;
    private AccountPhase _phase = AccountPhase.SignedOut;
    private string? _failureCode;
    private bool _closed;

    public AccountService(
        Context ctx,
        AccountConfig config,
        HarnessHome home,
        HttpMessageHandler? httpHandler = null,
        Func<string, bool>? openBrowser = null,
        ISecretProtector? protector = null) : base(ctx, ServiceName)
    {
        _config = config;
        _logger = ctx.LoggerFor(LogName);
        _openBrowser = openBrowser ?? BrowserLauncher.Open;
        Origin = ResolveOrigin(config);
        InferenceOrigin = AccountOrigins.InferenceOrigin(config.InferenceOrigin);
        _store = new AccountStore(home, protector);
        var handler = httpHandler ?? OutboundProxy.FromContext(ctx).CreateHandler(allowAutoRedirect: false);
        _client = new PlatformClient(handler, Origin, config.ClientPlatform, Version(), config.Locale, config.RequestTimeoutMs, disposeHandler: true, Log);
        ctx.Provide(IInferenceCredentials.ServiceName, this, Check);
    }

    public event Action? Changed;

    public event Action? SessionExpired;

    public string Origin { get; }

    public string InferenceOrigin { get; }

    /** 推理 origin 决定账号令牌能覆盖哪个 API base url; 是否真的给出令牌由登录态决定。 */
    Uri? IInferenceCredentials.InferenceOrigin => new(InferenceOrigin);

    public AccountSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new AccountSnapshot(_phase, _profile, _balance, _authorizeUrl, _failureCode);
        }
    }

    public async Task<AccountProfile?> SignInAsync(CancellationToken cancellationToken)
    {
        if (_closed)
            throw new AccountException(AccountFailure.Protocol);
        await CancelAsync();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource<AccountProfile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _attempt = attempt;
            _attemptCompletion = completion;
        }
        try
        {
            var profile = await RunSignInAsync(attempt.Token);
            completion.TrySetResult(profile);
            return profile;
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_attempt, attempt))
                {
                    _attempt = null;
                    _attemptCompletion = null;
                }
            }
        }
    }

    public async Task CancelAsync()
    {
        CancellationTokenSource? attempt;
        Task? completion;
        lock (_gate)
        {
            attempt = _attempt;
            completion = _attemptCompletion?.Task;
        }
        attempt?.Cancel();
        if (completion is not null)
        {
            await Task.WhenAny(completion);
            _ = completion.Exception;
        }
    }

    public async Task SignOutAsync()
    {
        await CancelAsync();
        var grant = _store.ReadGrant(Origin);
        _store.DeleteGrant();
        SetSignedOut();
        if (grant is not null)
            _ = RevokeAsync(grant.Token);
    }

    public async Task<AccountProfile?> GetProfileAsync(CancellationToken cancellationToken)
    {
        var grant = _store.ReadGrant(Origin);
        if (grant is null)
        {
            SetSignedOut();
            return null;
        }
        try
        {
            var profile = PlatformPayloads.ParseProfile(await _client.GetCurrentUserAsync(grant.Token, cancellationToken));
            lock (_gate)
            {
                _profile = profile;
                _phase = AccountPhase.SignedIn;
                _failureCode = null;
            }
            RaiseChanged();
            return profile;
        }
        catch (AccountException error) when (error.Failure == AccountFailure.Expired)
        {
            ExpireCredential(grant.Token);
            return null;
        }
    }

    public async Task<AccountBalance?> GetBalanceAsync(CancellationToken cancellationToken)
    {
        var grant = _store.ReadGrant(Origin);
        if (grant is null)
            return null;
        try
        {
            var balance = PlatformPayloads.ParseBalance(await _client.GetSummaryAsync(grant.Token, cancellationToken));
            lock (_gate)
                _balance = balance;
            RaiseChanged();
            return balance;
        }
        catch (AccountException error) when (error.Failure == AccountFailure.Expired)
        {
            ExpireCredential(grant.Token);
            return null;
        }
    }

    /** 在系统浏览器打开平台个人页(用量/充值): 这两项功能只有官网提供, 客户端不做本地复刻。 */
    public bool OpenPlatformPage(AccountPlatformPage page)
    {
        var path = page == AccountPlatformPage.TopUp ? AccountOrigins.TopUpPath : AccountOrigins.UsagePath;
        return OpenBrowser($"{Origin}{path}");
    }

    /** 注册阶段: 该目标是否由账号提供令牌(只比 origin, 不读取令牌)。 */
    public bool Covers(Uri destination) => IsInferenceOrigin(destination);

    /** 请求阶段: origin 匹配且存在同签发者的授权时给出令牌。 */
    public string? TryGetToken(Uri destination)
    {
        if (!Covers(destination))
            return null;
        var grant = _store.ReadGrant(Origin);
        return grant is not null && string.Equals(grant.Issuer, Origin, StringComparison.Ordinal)
            ? grant.Token
            : null;
    }

    /** 推理端点拒绝了该令牌: 只在它仍是当前登录时清除, 迟到的拒绝不能清掉后来重新登录的凭据。 */
    public void RejectToken(string token) => ExpireCredential(token, notify: false);

    private bool IsInferenceOrigin(Uri destination)
        => string.Equals(
            destination.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped),
            InferenceOrigin,
            StringComparison.Ordinal);

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        lock (_gate)
            _attempt?.Cancel();
        _client.Dispose();
    }

    private async Task<AccountProfile?> RunSignInAsync(CancellationToken cancellationToken)
    {
        var state = AccountPkce.NewState();
        var verifier = AccountPkce.NewVerifier();
        await using var callback = new LoopbackCallbackServer();
        callback.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_config.AttemptTimeoutMs);
        lock (_gate)
        {
            _authorizeId = null;
            _verifier = verifier;
        }
        try
        {
            SetPhase(AccountPhase.Initializing, null, null);
            var init = await _client.AuthInitAsync(
                new AuthInitRequest(AccountPkce.Challenge(verifier), state, callback.RedirectUri, _config.Locale, LoginSource), timeout.Token);
            var authorizeUrl = AccountOrigins.BrowserUrl(init.AuthorizeUrl, Origin, AccountOrigins.AuthorizePath);
            lock (_gate)
            {
                _authorizeId = init.AuthorizeId;
                _authorizeUrl = authorizeUrl;
            }
            timeout.CancelAfter(AttemptWindow(init.ExpiresIn));
            SetPhase(AccountPhase.WaitingBrowser, authorizeUrl, null);
            OpenBrowser(authorizeUrl);
            var code = await WaitForCodeAsync(callback, state, timeout);
            SetPhase(AccountPhase.Exchanging, authorizeUrl, null);
            var exchange = await ExchangeAsync(code, verifier, callback.RedirectUri, timeout.Token);
            OpenBrowser(AppendLoginSource(AccountOrigins.BrowserUrl(exchange.AuthorizedUrl, Origin, AccountOrigins.AuthorizedPath)));
            _store.WriteGrant(new AccountGrant(1, exchange.Token, Origin));
            var profile = exchange.User is { } user ? PlatformPayloads.ParseProfile(user) : null;
            lock (_gate)
            {
                _profile = profile;
                _balance = null;
                _phase = AccountPhase.SignedIn;
                _authorizeUrl = null;
                _failureCode = null;
            }
            RaiseChanged();
            return profile;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelRemoteAsync();
            SetPhase(AccountPhase.Cancelled, null, null);
            throw;
        }
        catch (OperationCanceledException)
        {
            await CancelRemoteAsync();
            SetPhase(AccountPhase.Failed, null, nameof(AccountFailure.Expired));
            throw new AccountException(AccountFailure.Expired);
        }
        catch (AccountException error)
        {
            await CancelRemoteAsync();
            SetPhase(AccountPhase.Failed, null, error.Failure.ToString());
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _authorizeId = null;
                _authorizeUrl = null;
                _verifier = null;
            }
        }
    }

    private static async Task<string> WaitForCodeAsync(LoopbackCallbackServer callback, string state, CancellationTokenSource timeout)
    {
        var code = await callback.WaitForCodeAsync(state, timeout.Token);
        if (code is not null)
            return code;
        timeout.Token.ThrowIfCancellationRequested();
        throw new AccountException(AccountFailure.Protocol);
    }

    private async Task<AuthExchangeResult> ExchangeAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        var deviceId = _store.GetOrCreateDeviceId();
        return await _client.AuthExchangeAsync(code, verifier, redirectUri, deviceId,
            AccountDeviceInfo.Model(), AccountDeviceInfo.OsVersion(), cancellationToken);
    }

    private Task RevokeAsync(string token)
    {
        var policy = new LogoutRetryPolicy(_config.LogoutMaxRetries, _config.LogoutRetryDelayMs, _config.RequestTimeoutMs);
        return LogoutRetrier.RunAsync(async cancellationToken =>
        {
            using var timeout = new CancellationTokenSource(policy.RequestTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await _client.LogoutAsync(token, linked.Token);
        }, policy, CancellationToken.None);
    }

    private async Task CancelRemoteAsync()
    {
        string? authorizeId;
        string? verifier;
        lock (_gate)
        {
            authorizeId = _authorizeId;
            verifier = _verifier;
        }
        if (authorizeId is null || verifier is null)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(_config.RequestTimeoutMs);
            await _client.CancelAsync(authorizeId, verifier, timeout.Token);
        }
        catch (Exception error)
        {
            Log($"auth_cancel failed failure={error.GetType().Name}");
        }
    }

    /** notify=false 用于推理端点的拒绝: 那次失败已经由请求本身报告, 不再额外抛一次会话失效事件。 */
    private void ExpireCredential(string token, bool notify = true)
    {
        var grant = _store.ReadGrant(Origin);
        if (grant is null || !string.Equals(grant.Token, token, StringComparison.Ordinal))
            return;
        _store.DeleteGrant();
        lock (_gate)
        {
            _profile = null;
            _balance = null;
            _phase = AccountPhase.SignedOut;
            _failureCode = nameof(AccountFailure.Expired);
        }
        RaiseChanged();
        if (notify)
            SessionExpired?.Invoke();
    }

    private void SetSignedOut()
    {
        lock (_gate)
        {
            _profile = null;
            _balance = null;
            _phase = AccountPhase.SignedOut;
            _authorizeUrl = null;
            _failureCode = null;
        }
        RaiseChanged();
    }

    private void SetPhase(AccountPhase phase, string? authorizeUrl, string? failureCode)
    {
        lock (_gate)
        {
            _phase = phase;
            _authorizeUrl = authorizeUrl;
            _failureCode = failureCode;
        }
        RaiseChanged();
    }

    private bool OpenBrowser(string url)
    {
        if (_openBrowser(url))
            return true;
        var path = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : "unknown";
        Log($"browser open failed path={path}");
        return false;
    }

    private static string AppendLoginSource(string url)
    {
        var builder = new UriBuilder(url);
        var query = builder.Query.TrimStart('?');
        builder.Query = query.Length == 0 ? $"login_source={LoginSource}" : $"{query}&login_source={LoginSource}";
        return builder.Uri.AbsoluteUri;
    }

    private TimeSpan AttemptWindow(int expiresInSeconds)
    {
        var max = TimeSpan.FromMilliseconds(_config.AttemptTimeoutMs);
        if (expiresInSeconds <= 0)
            return max;
        var expires = TimeSpan.FromSeconds(expiresInSeconds);
        return expires < max ? expires : max;
    }

    private void RaiseChanged() => Changed?.Invoke();

    private void Log(string text) => _logger.Info("%s", text);

    private static string Version() => typeof(AccountService).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private string ResolveOrigin(AccountConfig config)
    {
        try
        {
            return AccountOrigins.PlatformOrigin(config.PlatformOrigin, config.AllowLoopbackHttp);
        }
        catch (AccountException)
        {
            Log("platformOrigin rejected; falling back to the default official origin");
            return AccountConfig.DefaultPlatformOrigin;
        }
    }
}
