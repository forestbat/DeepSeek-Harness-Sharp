using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Account;

namespace Dsh.Gui.ViewModels;

/** 设置页「账号」区状态机: 只消费 IAccountService, 不直接接触协议与凭据。 */
public sealed partial class AccountPanelViewModel : ObservableObject
{
    private const string DisclaimerText =
        "非官方功能：本功能通过 DeepSeek 网页端接口/登录态工作，未经 DeepSeek 官方授权，接口可能在无通知的情况下失效。\n"
        + "登录态仅保存在本机（加密），不会上传、不会写入日志。请勿在共享电脑上使用。\n"
        + "使用第三方客户端登录可能导致账号被限流、禁言或封禁，风险自负。\n"
        + "如需官方支持，请使用 DeepSeek 开放平台 API Key（platform.deepseek.com）。";

    private readonly IAccountService? _service;

    private CancellationTokenSource? _signIn;

    public AccountPanelViewModel(IAccountService? service)
    {
        _service = service;
        SyncFromService();
    }

    public bool IsAvailable => _service is not null;

    public string Disclaimer => DisclaimerText;

    public bool IsSignedOut => Stage == AccountPhase.SignedOut;

    public bool IsWaiting => Stage is AccountPhase.Initializing or AccountPhase.WaitingBrowser or AccountPhase.Exchanging;

    public bool IsSignedIn => Stage == AccountPhase.SignedIn;

    public bool IsCancelled => Stage == AccountPhase.Cancelled;

    public bool IsFailed => Stage == AccountPhase.Failed;

    public bool CanSignIn => IsAvailable && !IsBusy;

    public bool CanCancel => IsAvailable && IsWaiting;

    /** 标题栏的账号按钮: 未登录时提示登录, 登录中提示进度, 已登录显示账号名。 */
    public string TitleText => Stage switch
    {
        AccountPhase.SignedIn => DisplayName.Length > 0 ? DisplayName : "我的账号",
        AccountPhase.Initializing or AccountPhase.WaitingBrowser or AccountPhase.Exchanging => "登录中…",
        _ => "登录到 DeepSeek",
    };

    public string TitleTip => Stage == AccountPhase.SignedIn
        ? "在浏览器打开 DeepSeek 用量与充值页面"
        : "在浏览器完成 DeepSeek 账号登录，之后可直接用账号额度对话";

    public bool HasBalance => BalanceText.Length > 0;

    public bool HasBonus => BonusText.Length > 0;

    public string Initial => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(IsSignedIn))]
    [NotifyPropertyChangedFor(nameof(IsCancelled))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(TitleText))]
    [NotifyPropertyChangedFor(nameof(TitleTip))]
    private AccountPhase _stage = AccountPhase.SignedOut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial))]
    [NotifyPropertyChangedFor(nameof(TitleText))]
    private string _displayName = "";

    [ObservableProperty]
    private string _maskedContact = "";

    [ObservableProperty]
    private string? _avatarUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBalance))]
    private string _balanceText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBonus))]
    private string _bonusText = "";

    /** 切到账号分区或窗口重开时调用: 有授权则拉取资料与余额, 无则保持未登录。 */
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        SyncFromService();
        if (_service is null)
            return;
        try
        {
            var profile = await _service.GetProfileAsync(cancellationToken);
            if (profile is null)
            {
                if (Stage != AccountPhase.Failed)
                    Stage = AccountPhase.SignedOut;
                ResetProfile();
                return;
            }
            ApplyProfile(profile);
            ApplyBalance(await _service.GetBalanceAsync(cancellationToken));
            Stage = AccountPhase.SignedIn;
            Status = "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (AccountException error)
        {
            Stage = error.Failure == AccountFailure.Expired ? AccountPhase.SignedOut : AccountPhase.Failed;
            Status = FailureText(error.Failure);
        }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (_service is null)
            return;
        _signIn = new CancellationTokenSource();
        IsBusy = true;
        Status = "";
        Stage = AccountPhase.Initializing;
        try
        {
            var profile = await _service.SignInAsync(_signIn.Token);
            profile ??= await _service.GetProfileAsync(_signIn.Token);
            if (profile is not null)
                ApplyProfile(profile);
            ApplyBalance(await _service.GetBalanceAsync(_signIn.Token));
            Stage = AccountPhase.SignedIn;
            Status = "";
        }
        catch (OperationCanceledException)
        {
            Stage = AccountPhase.Cancelled;
            Status = "已取消登录";
        }
        catch (AccountException error)
        {
            Stage = AccountPhase.Failed;
            Status = FailureText(error.Failure);
        }
        finally
        {
            IsBusy = false;
            _signIn?.Dispose();
            _signIn = null;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (_service is null)
            return;
        await _service.CancelAsync();
        Stage = AccountPhase.Cancelled;
        Status = "已取消登录";
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (_service is null)
            return;
        IsBusy = true;
        try
        {
            await _service.SignOutAsync();
            ResetProfile();
            Stage = AccountPhase.SignedOut;
            Status = "已退出登录";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /** 用量明细只有平台官网有: 「更多账号信息」与「查询用量」都跳到同一个官方页面。 */
    [RelayCommand]
    private void OpenUsage() => OpenPlatformPage(AccountPlatformPage.Usage);

    [RelayCommand]
    private void OpenTopUp() => OpenPlatformPage(AccountPlatformPage.TopUp);

    /**
     * 标题栏账号按钮: 未登录直接拉起浏览器登录, 不进设置页中转; 已登录则打开平台个人页。
     * 按钮始终存在, 不随登录态消失。
     */
    [RelayCommand]
    private async Task TitleAsync()
    {
        if (IsWaiting)
            return;
        if (Stage == AccountPhase.SignedIn)
        {
            OpenPlatformPage(AccountPlatformPage.Usage);
            return;
        }
        await SignInAsync();
    }

    private void OpenPlatformPage(AccountPlatformPage page)
    {
        if (_service is null)
            return;
        Status = _service.OpenPlatformPage(page) ? "" : "无法打开浏览器，请手动访问 platform.deepseek.com";
    }

    private void SyncFromService()
    {
        if (_service is null)
            return;
        Stage = _service.Snapshot.Phase == AccountPhase.SignedIn && _service.Snapshot.Profile is null
            ? AccountPhase.SignedOut
            : _service.Snapshot.Phase;
    }

    private void ApplyProfile(AccountProfile profile)
    {
        DisplayName = profile.Name ?? profile.Id ?? "已登录";
        MaskedContact = profile.MaskedContact;
        AvatarUrl = profile.AvatarUrl;
    }

    private void ResetProfile()
    {
        DisplayName = "";
        MaskedContact = "";
        AvatarUrl = null;
        BalanceText = "";
        BonusText = "";
    }

    private void ApplyBalance(AccountBalance? balance)
    {
        if (balance is null)
        {
            BalanceText = "";
            BonusText = "";
            return;
        }
        BalanceText = Amounts(balance.NormalWallets);
        BonusText = balance.BonusWallets.Count == 0 ? "" : $"赠金 {Amounts(balance.BonusWallets)}";
    }

    private static string Amounts(IReadOnlyList<AccountWallet> wallets)
        => wallets.Count == 0 ? "" : string.Join(" · ", wallets.Select(wallet => $"{Symbol(wallet.Currency)}{wallet.Balance}"));

    private static string Symbol(string currency) => currency switch
    {
        "CNY" => "¥",
        "USD" => "$",
        _ => $"{currency} ",
    };

    private static string FailureText(AccountFailure failure) => failure switch
    {
        AccountFailure.Network => "网络不可用或平台未响应",
        AccountFailure.Protocol => "平台响应不符合预期（接口可能已变更）",
        AccountFailure.Storage => "本地凭据读写失败",
        AccountFailure.Expired => "登录态已过期，请重新登录",
        _ => "登录失败",
    };
}
