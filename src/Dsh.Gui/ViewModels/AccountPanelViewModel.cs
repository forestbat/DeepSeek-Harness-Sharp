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

    public bool CanSignIn => IsAvailable && IsEnabled && !IsBusy;

    public bool CanCancel => IsAvailable && IsWaiting;

    public string Initial => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(IsSignedIn))]
    [NotifyPropertyChangedFor(nameof(IsCancelled))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private AccountPhase _stage = AccountPhase.SignedOut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial))]
    private string _displayName = "";

    [ObservableProperty]
    private string _maskedContact = "";

    [ObservableProperty]
    private string? _avatarUrl;

    [ObservableProperty]
    private string _balanceText = "";

    partial void OnIsEnabledChanged(bool value) => _service?.SetEnabled(value);

    /** 切到账号分区或窗口重开时调用: 有授权则拉取资料与余额, 无则保持未登录。 */
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        SyncFromService();
        if (_service is null || !_service.Enabled)
            return;
        try
        {
            var profile = await _service.GetProfileAsync(cancellationToken);
            if (profile is null)
            {
                if (Stage != AccountPhase.Failed)
                    Stage = AccountPhase.SignedOut;
                return;
            }
            ApplyProfile(profile);
            BalanceText = FormatBalance(await _service.GetBalanceAsync(cancellationToken));
            Stage = AccountPhase.SignedIn;
            Status = "已登录";
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
            BalanceText = FormatBalance(await _service.GetBalanceAsync(_signIn.Token));
            Stage = AccountPhase.SignedIn;
            Status = "已登录";
        }
        catch (OperationCanceledException)
        {
            Stage = AccountPhase.Cancelled;
            Status = "已取消登录";
        }
        catch (AccountException error) when (error.Failure == AccountFailure.Disabled)
        {
            Stage = AccountPhase.SignedOut;
            Status = "请先打开账号功能总开关";
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

    private void SyncFromService()
    {
        if (_service is null)
            return;
        IsEnabled = _service.Enabled;
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
    }

    private static string FormatBalance(AccountBalance? balance)
    {
        if (balance is null)
            return "";
        var parts = balance.NormalWallets.Select(wallet => $"{wallet.Currency} {wallet.Balance}").ToList();
        parts.AddRange(balance.BonusWallets.Select(wallet => $"赠金 {wallet.Currency} {wallet.Balance}"));
        return parts.Count == 0 ? "余额不可用" : string.Join(" · ", parts);
    }

    private static string FailureText(AccountFailure failure) => failure switch
    {
        AccountFailure.Disabled => "账号功能未开启",
        AccountFailure.Network => "网络不可用或平台未响应",
        AccountFailure.Protocol => "平台响应不符合预期（接口可能已变更）",
        AccountFailure.Storage => "本地凭据读写失败",
        AccountFailure.Expired => "登录态已过期，请重新登录",
        _ => "登录失败",
    };
}
