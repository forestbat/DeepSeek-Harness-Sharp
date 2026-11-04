using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Core;
using Dsh.Interaction;

namespace Dsh.Gui.ViewModels;

public enum DecisionKind
{
    Approval,
    Question,
}

/** Presentation adapters over the shared choice state; a decision resolves at most once. */
public sealed partial class DecisionViewModel : ObservableObject
{
    private DecisionViewModel(DecisionKind kind, string? requestId)
    {
        Kind = kind;
        RequestId = requestId;
        SubmitCommand = new RelayCommand(() => Resolve(BuildAnswer()), () => CanSubmit);
        AllowOnceCommand = new RelayCommand(() => ResolveApproval(0), () => CanResolveApproval(0));
        AllowAlwaysCommand = new RelayCommand(() => ResolveApproval(1), () => CanResolveApproval(1));
        RejectCommand = new RelayCommand(() => ResolveApproval(2), () => CanResolveApproval(2));
        CancelCommand = new RelayCommand(() => Resolve(null), () => !IsClosed && SupportsCancellation);
    }

    public DecisionKind Kind { get; }
    public string? RequestId { get; }
    public ApprovalRequest? Approval { get; private init; }
    public ChoicePromptState? ApprovalState { get; private init; }
    public IReadOnlyList<DecisionQuestionViewModel> Questions { get; private set; } = [];
    public RelayCommand SubmitCommand { get; }
    public RelayCommand AllowOnceCommand { get; }
    public RelayCommand AllowAlwaysCommand { get; }
    public RelayCommand RejectCommand { get; }
    public RelayCommand CancelCommand { get; }
    public object? Result { get; private set; }
    public bool IsClosed { get; private set; }
    public bool IsSuperseded { get; private set; }
    public bool SupportsCancellation { get; private set; } = true;
    public string CancellationSupportMessage => "Unsupported：此 Host 不支持取消，请先升级 Host；请求仍未决。";
    public event Action<DecisionViewModel>? Closed;
    public bool IsApproval => Kind == DecisionKind.Approval;
    public bool IsQuestion => Kind == DecisionKind.Question;
    public string Heading => IsApproval ? "需要审批" : "需要你决定";
    public string QuestionSummary => IsApproval ? "智能体请求执行工具" : "智能体向你提问";
    public string ToolName { get; private init; } = "";
    public string Command { get; private init; } = "";
    public string Reason { get; private init; } = "";
    public string Impact { get; private init; } = "";
    public bool HasImpact => Impact.Length > 0;
    public bool CanSubmit => !IsClosed && Questions.Count > 0 && Questions.All(question => question.IsAnswered);
    public string Arguments { get; private init; } = "";

    public static DecisionViewModel ForApproval(ApprovalRequest request, string? requestId = null)
        => new(DecisionKind.Approval, requestId)
        {
            Approval = request,
            ApprovalState = new(ChoicePrompt.ForApproval(request.ToolName, request.Arguments, request.Reason, requestId)),
            ToolName = request.ToolName,
            Command = ApprovalHints.PrimaryArgument(request.ToolName, request.Arguments),
            Arguments = request.Arguments ?? "",
            Reason = request.Reason ?? "",
            Impact = ApprovalHints.Impact(request.ToolName, request.Arguments),
        };

    public static DecisionViewModel ForApproval(string toolName, string? arguments, string? reason, string? requestId = null, bool allowOnce = true, bool allowForSession = true, bool reject = true)
        => new(DecisionKind.Approval, requestId)
        {
            ApprovalState = new(ChoicePrompt.ForApproval(toolName, arguments, reason, requestId, allowOnce, allowForSession, reject)),
            ToolName = toolName,
            Command = ApprovalHints.PrimaryArgument(toolName, arguments),
            Arguments = arguments ?? "",
            Reason = reason ?? "",
            Impact = ApprovalHints.Impact(toolName, arguments),
        };

    public static DecisionViewModel ForQuestion(AskUserQuestionRequest request, string? requestId = null)
    {
        var viewModel = new DecisionViewModel(DecisionKind.Question, requestId);
        viewModel.Questions = [.. request.Questions.Select(question => new DecisionQuestionViewModel(question, requestId, () => !viewModel.IsClosed))];
        foreach (var question in viewModel.Questions)
            question.Changed += viewModel.RefreshCanSubmit;
        return viewModel;
    }

    public AskUserQuestionAnswer? BuildAnswer()
        => Questions.Count == 0 || Questions.Any(question => !question.IsAnswered)
            ? null : new([.. Questions.Select(question => question.BuildAnswer())]);

    public void SetCancellationSupported(bool supported)
    {
        if (SupportsCancellation == supported)
            return;
        SupportsCancellation = supported;
        OnPropertyChanged(nameof(SupportsCancellation));
        CancelCommand.NotifyCanExecuteChanged();
    }

    /** Host resolution invalidates the dialog without submitting a new answer. */
    public void Invalidate()
    {
        IsSuperseded = true;
        Resolve(null);
    }

    public void Resolve(object? result)
    {
        if (IsClosed)
            return;
        if (!SupportsCancellation && !IsSuperseded
            && (result is null or ApprovalOutcome.Cancelled || result is AskUserQuestionAnswer { IsDismissed: true }))
            return;
        if (result is null)
        {
            if (IsApproval)
                ApprovalState?.Select(3);
            result = IsApproval ? ApprovalOutcome.Cancelled : AskUserQuestionAnswer.Dismissed;
        }
        Result = result;
        IsClosed = true;
        OnPropertyChanged(nameof(IsClosed));
        RefreshCanSubmit();
        AllowOnceCommand.NotifyCanExecuteChanged();
        AllowAlwaysCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        Closed?.Invoke(this);
    }

    private bool CanResolveApproval(int index)
        => !IsClosed && ApprovalState is { } state && !state.Prompt.Options[index].Disabled;

    private void ResolveApproval(int index)
    {
        if (!CanResolveApproval(index) || ApprovalState is not { } state)
            return;
        state.Select(index);
        Resolve(state.SelectedValues().Single() switch
        {
            "once" => ApprovalOutcome.AllowedOnce,
            "session" => ApprovalOutcome.AllowedForSession,
            "reject" => ApprovalOutcome.Rejected,
            "cancel" => ApprovalOutcome.Cancelled,
            _ => throw new InvalidOperationException("Unknown approval choice."),
        });
    }

    private void RefreshCanSubmit()
    {
        OnPropertyChanged(nameof(CanSubmit));
        SubmitCommand.NotifyCanExecuteChanged();
    }
}

/** Adapts choice state for bindings without maintaining another selection implementation. */
public sealed partial class DecisionQuestionViewModel : ObservableObject
{
    private readonly Func<bool> _isActive;

    public DecisionQuestionViewModel(AskUserQuestionItem question, string? requestId = null, Func<bool>? isActive = null)
    {
        Item = question;
        State = new(ChoicePrompt.FromQuestion(question, requestId));
        _isActive = isActive ?? (() => true);
        Options = [.. State.Prompt.Options.Select((option, index) => new DecisionOptionViewModel(option, State, index))];
        foreach (var option in Options)
            option.SelectCommand = new RelayCommand(() => Select(option));
    }

    public AskUserQuestionItem Item { get; }
    public ChoicePromptState State { get; }
    public ObservableCollection<DecisionOptionViewModel> Options { get; }
    public event Action? Changed;
    public string Question => Item.Question;
    public string Detail => Item.Detail ?? "";
    public bool HasDetail => Detail.Length > 0;
    public string Header => Item.Header ?? "";
    public bool HasHeader => Header.Length > 0;
    public bool MultiSelect => State.Prompt.MultiSelect;
    public bool AllowCustom => State.Prompt.AllowCustom;
    public string ApproveLabel => Item.Intent?.Approve ?? "";
    public string CustomText
    {
        get => State.CustomText;
        set
        {
            if (!_isActive())
                return;
            State.CustomText = value ?? "";
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }
    public bool IsAnswered => State.CanSubmit;

    [RelayCommand]
    private void Select(DecisionOptionViewModel? option)
    {
        if (option is null || !_isActive())
            return;
        var index = Options.IndexOf(option);
        State.Select(index);
        foreach (var candidate in Options)
            candidate.Refresh();
        Changed?.Invoke();
    }

    public AskUserQuestionAnswerItem BuildAnswer() => State.BuildAnswer(Item.Id);
}

public sealed class DecisionOptionViewModel(ChoicePromptOption option, ChoicePromptState state, int index) : ObservableObject
{
    public string Label => option.Label;
    public string Description => option.Description ?? "";
    public bool HasDescription => Description.Length > 0;
    public bool IsEnabled => !option.Disabled;
    public bool IsSelected => state.IsSelected(index);
    public RelayCommand? SelectCommand { get; internal set; }
    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}
