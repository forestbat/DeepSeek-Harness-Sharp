using System.Net;
using System.Text;
using Dsh.Account;
using Dsh.Boot;

namespace Dsh.Tests;

internal sealed record AccountHttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string? Body);

/** 注入式 HTTP 边界: 记录方法/路径/头/正文, 便于断言协议与客户端身份头。 */
internal sealed class AccountHttpHandler(Func<AccountHttpRequest, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<AccountHttpRequest> Requests { get; } = [];

    public static HttpResponseMessage Json(string payload, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

    public static string Envelope(string bizData)
        => "{\"code\":0,\"data\":{\"biz_code\":0,\"biz_data\":" + bizData + "}}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
            headers[header.Key] = string.Join(",", header.Value);
        var record = new AccountHttpRequest(request.Method.Method, request.RequestUri?.AbsolutePath ?? "", headers, body);
        Requests.Add(record);
        return Task.FromResult(responder(record));
    }
}

/** 每个测试独立 home, 账号存储不会串味。 */
internal sealed class AccountTempHome : IDisposable
{
    public AccountTempHome()
    {
        Root = Path.Combine(Path.GetTempPath(), $"dsh-account-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Home = HarnessHome.Resolve(Root);
    }

    public string Root { get; }

    public HarnessHome Home { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/** VM 层假账号服务: 只实现 seam 行为, 不复制任何协议逻辑。 */
internal sealed class FakeAccountService : IAccountService
{
    private CancellationTokenSource? _attempt;

    public event Action? Changed;

    public event Action? SessionExpired;

    public bool Enabled { get; set; } = true;

    public AccountSnapshot Snapshot { get; set; } = new(AccountPhase.SignedOut, true, null, null, null, null);

    public Func<CancellationToken, Task<AccountProfile?>> SignInHandler { get; set; } = _ => Task.FromResult<AccountProfile?>(null);

    public Func<CancellationToken, Task<AccountProfile?>> ProfileHandler { get; set; } = _ => Task.FromResult<AccountProfile?>(null);

    public Func<CancellationToken, Task<AccountBalance?>> BalanceHandler { get; set; } = _ => Task.FromResult<AccountBalance?>(null);

    public Task SignOutAsync()
    {
        Snapshot = Snapshot with { Phase = AccountPhase.SignedOut, Profile = null };
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        Changed?.Invoke();
    }

    public Task<AccountProfile?> SignInAsync(CancellationToken cancellationToken)
    {
        _attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return SignInHandler(_attempt.Token);
    }

    public Task CancelAsync()
    {
        _attempt?.Cancel();
        return Task.CompletedTask;
    }

    public Task<AccountProfile?> GetProfileAsync(CancellationToken cancellationToken) => ProfileHandler(cancellationToken);

    public Task<AccountBalance?> GetBalanceAsync(CancellationToken cancellationToken) => BalanceHandler(cancellationToken);

    public void RaiseSessionExpired() => SessionExpired?.Invoke();
}
