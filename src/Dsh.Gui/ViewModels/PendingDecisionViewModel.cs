using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Workspace;
using Dsh.Interaction;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/** Host pending state owns the lifetime; this adapter only presents and sends an answer. */
public sealed partial class PendingDecisionViewModel(FleetPendingItem item, Func<IWorkspaceMachine?> machine) : ObservableObject
{
    private DecisionViewModel? _dialog;
    private bool _resolved;

    public FleetPendingItem Item { get; } = item;
    public string Machine => Item.Machine;
    public string RequestId => Item.RequestId;
    public string SessionId => Item.SessionId;
    public bool IsApproval => Item.Kind == PendingKinds.Approval;
    public bool IsQuestion => Item.Kind == PendingKinds.Question;
    public string KindLabel => IsApproval ? "审批" : "提问";
    public string Title => Item.Approval is { } approval
        ? $"{approval.Tool} {ApprovalHints.PrimaryArgument(approval.Tool, approval.Arguments)}".Trim()
        : Item.Question?.Questions.FirstOrDefault()?.Question ?? "";
    public string Detail => Item.Approval?.Reason ?? Item.Question?.Questions.FirstOrDefault()?.Detail ?? "";
    public bool HasDetail => Detail.Length > 0;
    public string Arguments => Item.Approval?.Arguments ?? "";
    public bool HasArguments => Arguments.Length > 0;
    public string Impact { get; } = item.Approval is { } approval
        ? approval.Impact ?? ApprovalHints.Impact(approval.Tool, approval.Arguments) : "";
    public bool HasImpact => Impact.Length > 0;
    public bool SupportsCancellation => machine()?.Hello?.Capabilities?.Contains(DecisionProtocol.Capability) == true;
    public bool CanCancel => !_resolved && !IsBusy && SupportsCancellation;
    private bool CanRespondApproval => IsApproval && !_resolved && !IsBusy;
    public bool CanAllow => CanRespondApproval && (Item.Approval?.Availability?.AllowOnce ?? true);
    public bool CanAlways => CanRespondApproval && (Item.Approval?.Availability?.AllowForSession ?? true);
    public bool CanReject => CanRespondApproval && (Item.Approval?.Availability?.Reject ?? true);
    public string CancellationSupportMessage => "Unsupported：此 Host 不支持取消，请先升级 Host；请求仍未决。";

    [ObservableProperty]
    private string _sessionTitle = "";

    [ObservableProperty]
    private bool _isBusy;

    public event Action<PendingDecisionViewModel>? Superseded;
    public event Func<DecisionViewModel, Task<object?>>? AnswerRequested;
    public event Action<PendingDecisionViewModel>? OpenRequested;

    [RelayCommand(CanExecute = nameof(CanAllow))]
    private Task AllowAsync() => RespondApprovalAsync(true);

    [RelayCommand(CanExecute = nameof(CanAlways))]
    private Task AlwaysAsync() => RespondApprovalAsync(true, rememberForSession: true);

    [RelayCommand(CanExecute = nameof(CanReject))]
    private Task RejectAsync() => RespondApprovalAsync(false);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private Task CancelAsync()
    {
        if (!CanCancel)
            return Task.CompletedTask;
        return IsApproval ? RespondApprovalAsync(false, cancelled: true) : SendDismissedAsync();
    }

    [RelayCommand]
    private void Open() => OpenRequested?.Invoke(this);

    /** Called before removing the item from the authoritative pending projection. */
    public void Invalidate()
    {
        _resolved = true;
        _dialog?.Invalidate();
        IsBusy = true;
    }

    public void RefreshCapabilities()
    {
        OnPropertyChanged(nameof(SupportsCancellation));
        CancelCommand.NotifyCanExecuteChanged();
        _dialog?.SetCancellationSupported(SupportsCancellation);
    }

    partial void OnIsBusyChanged(bool value)
    {
        AllowCommand.NotifyCanExecuteChanged();
        AlwaysCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task AnswerAsync()
    {
        if (_resolved || IsBusy || Item.Question is not { } question || AnswerRequested is not { } ask || machine() is not { } connection)
            return;
        IsBusy = true;
        var decision = DecisionViewModel.ForQuestion(new AskUserQuestionRequest([.. question.Questions.Select(ToItem)]), RequestId);
        decision.SetCancellationSupported(SupportsCancellation);
        _dialog = decision;
        try
        {
            var result = await ask(decision);
            if (_resolved || decision.IsSuperseded)
                return;
            var answer = result as AskUserQuestionAnswer ?? AskUserQuestionAnswer.Dismissed;
            await SendCoreAsync(connection, (host, token) => host.RespondQuestionAsync(
                RequestId, [.. answer.Answers.Select(entry => new RemoteQuestionAnswerItem(entry.Id, entry.Selected, entry.Custom))], token,
                status: answer.Status, reason: answer.Reason));
        }
        finally
        {
            _dialog = null;
            IsBusy = _resolved;
        }
    }

    private Task RespondApprovalAsync(bool allow, bool rememberForSession = false, bool cancelled = false)
    {
        var permitted = (allow, rememberForSession, cancelled) switch
        {
            (_, _, true) => CanCancel,
            (true, true, _) => CanAlways,
            (true, false, _) => CanAllow,
            _ => CanReject,
        };
        return permitted && machine() is { } connection
            ? SendAsync(connection, (host, token) => host.RespondApprovalAsync(RequestId, allow, null, token,
                rememberForSession: rememberForSession, cancelled: cancelled))
            : Task.CompletedTask;
    }

    private Task SendDismissedAsync()
        => machine() is { } connection
            ? SendAsync(connection, (host, token) => host.RespondQuestionAsync(RequestId, [], token,
                status: AskUserQuestionAnswer.Dismissed.Status, reason: AskUserQuestionAnswer.Dismissed.Reason))
            : Task.CompletedTask;

    private async Task SendAsync(IWorkspaceMachine connection, Func<IWorkspaceHost, CancellationToken, Task<bool>> respond)
    {
        if (_resolved)
        {
            Superseded?.Invoke(this);
            return;
        }
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await SendCoreAsync(connection, respond);
        }
        finally
        {
            IsBusy = _resolved;
        }
    }

    private async Task SendCoreAsync(IWorkspaceMachine connection, Func<IWorkspaceHost, CancellationToken, Task<bool>> respond)
    {
        var lease = await connection.WaitConnectedAsync(CancellationToken.None);
        if (_resolved)
            return;
        var applied = await respond(lease.Client, CancellationToken.None);
        if (applied)
            Invalidate();
    }

    internal static AskUserQuestionItem ToItem(RemoteQuestionItem item)
        => new(
            item.Id,
            item.Question,
            item.Detail,
            item.Header,
            [.. item.Options.Select(option => new AskUserQuestionOption(option.Label, option.Description, option.Value, option.Disabled, option.Default))],
            item.MultiSelect,
            item.ApproveLabel is { } approve ? new AskUserQuestionIntent(AskUserQuestionIntentKind.PlanReview, approve) : null,
            item.AllowCustom);
}
