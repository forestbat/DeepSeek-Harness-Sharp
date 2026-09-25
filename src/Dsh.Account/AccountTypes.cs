namespace Dsh.Account;

/** 账号交互阶段: GUI 只读它决定展示哪一屏。 */
public enum AccountPhase
{
    SignedOut,
    Initializing,
    WaitingBrowser,
    Exchanging,
    SignedIn,
    Cancelled,
    Failed,
}

/** 账号失败分类: 不含任何响应正文或 URL, 可安全进 UI 与日志。 */
public enum AccountFailure
{
    Disabled,
    Network,
    Protocol,
    Storage,
    Expired,
}

/** 账号协议错误: 消息只暴露稳定分类, 绝不携带 token/正文/授权 URL。 */
public sealed class AccountException(AccountFailure failure) : Exception($"account: {failure}")
{
    public AccountFailure Failure { get; } = failure;
}

/** 平台账号资料: contact 为手机号或邮箱原文, UI 一律显示脱敏值。 */
public sealed record AccountProfile(string? Id, string? Name, string? AvatarUrl, string? Contact)
{
    public string MaskedContact => AccountMasking.MaskContact(Contact);
}

/** 钱包余额: 金额保持平台返回的字符串原样。 */
public sealed record AccountWallet(string Currency, string Balance);

public sealed record AccountBalance(IReadOnlyList<AccountWallet> NormalWallets, IReadOnlyList<AccountWallet> BonusWallets);

/** 账号状态快照: GUI 消费的唯一状态形态。 */
public sealed record AccountSnapshot(
    AccountPhase Phase,
    bool Enabled,
    AccountProfile? Profile,
    AccountBalance? Balance,
    string? AuthorizeUrl,
    string? FailureCode);

/** 账号服务 seam: GUI 只依赖它, 便于 VM 层注入假实现。 */
public interface IAccountService
{
    event Action? Changed;

    event Action? SessionExpired;

    bool Enabled { get; }

    AccountSnapshot Snapshot { get; }

    void SetEnabled(bool enabled);

    Task<AccountProfile?> SignInAsync(CancellationToken cancellationToken);

    Task CancelAsync();

    Task SignOutAsync();

    Task<AccountProfile?> GetProfileAsync(CancellationToken cancellationToken);

    Task<AccountBalance?> GetBalanceAsync(CancellationToken cancellationToken);
}
