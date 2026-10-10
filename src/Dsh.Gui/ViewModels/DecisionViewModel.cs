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

/** 统一「需要你决定」窗口的状态: 审批与 ask_user_question 共用同一套交互。 */
public sealed partial class DecisionViewModel : ObservableObject
{
    private DecisionViewModel(DecisionKind kind)
    {
        Kind = kind;
        SubmitCommand = new RelayCommand(() => Resolve(BuildAnswerOrNull()), () => CanSubmit);
        AllowOnceCommand = new RelayCommand(() => Resolve(ApprovalOutcome.AllowedOnce));
        AllowAlwaysCommand = new RelayCommand(() => Resolve(ApprovalOutcome.AllowedForSession));
        RejectCommand = new RelayCommand(() => Resolve(ApprovalOutcome.Rejected));
        CancelCommand = new RelayCommand(() => Resolve(null));
    }

    public DecisionKind Kind { get; }

    public ApprovalRequest? Approval { get; init; }

    public IReadOnlyList<DecisionQuestionViewModel> Questions { get; init; } = [];

    public RelayCommand SubmitCommand { get; }

    public RelayCommand AllowOnceCommand { get; }

    public RelayCommand AllowAlwaysCommand { get; }

    public RelayCommand RejectCommand { get; }

    public RelayCommand CancelCommand { get; }

    /** 弹窗结论: 审批为 ApprovalOutcome, 问题为 AskUserQuestionAnswer, 取消为 null。 */
    public object? Result { get; private set; }

    public event Action<DecisionViewModel>? Closed;

    public bool IsApproval => Kind == DecisionKind.Approval;

    public bool IsQuestion => Kind == DecisionKind.Question;

    public string Heading => IsApproval ? "需要审批" : "需要你决定";

    public string QuestionSummary => IsApproval ? "智能体请求执行工具" : "智能体向你提问";

    [ObservableProperty]
    private string _toolName = "";

    [ObservableProperty]
    private string _command = "";

    [ObservableProperty]
    private string _reason = "";

    [ObservableProperty]
    private string _impact = "";

    [ObservableProperty]
    private bool _hasImpact;

    [ObservableProperty]
    private bool _rememberForSession;

    [ObservableProperty]
    private bool _canSubmit;

    public static DecisionViewModel ForApproval(ApprovalRequest request)
    {
        var command = ApprovalHints.PrimaryArgument(request.ToolName, request.Arguments);
        var impact = ApprovalHints.Impact(request.ToolName, request.Arguments);
        return new DecisionViewModel(DecisionKind.Approval)
        {
            Approval = request,
            ToolName = request.ToolName,
            Command = command,
            Reason = request.Reason ?? "",
            Impact = impact,
            HasImpact = impact.Length > 0,
        };
    }

    /** 远端审批没有本地 ApprovalRequest(无 IAgent): 用工具名/参数/理由构造同一个弹窗。 */
    public static DecisionViewModel ForApproval(string toolName, string? arguments, string? reason)
    {
        var impact = ApprovalHints.Impact(toolName, arguments);
        return new DecisionViewModel(DecisionKind.Approval)
        {
            ToolName = toolName,
            Command = ApprovalHints.PrimaryArgument(toolName, arguments),
            Reason = reason ?? "",
            Impact = impact,
            HasImpact = impact.Length > 0,
        };
    }

    public static DecisionViewModel ForQuestion(AskUserQuestionRequest request)
    {
        var viewModel = new DecisionViewModel(DecisionKind.Question)
        {
            Questions = [.. request.Questions.Select(question => new DecisionQuestionViewModel(question))],
        };
        foreach (var question in viewModel.Questions)
            question.Changed += viewModel.RefreshCanSubmit;
        viewModel.RefreshCanSubmit();
        return viewModel;
    }

    public AskUserQuestionAnswer? BuildAnswer()
        => Questions.Count == 0 ? null : new AskUserQuestionAnswer([.. Questions.Select(question => question.BuildAnswer())]);

    /** 键盘 n/y/a/c 与按钮共用的收口点。 */
    public void Resolve(object? result)
    {
        if (Kind == DecisionKind.Approval && result is null)
            result = ApprovalOutcome.Cancelled;
        Result = result;
        Closed?.Invoke(this);
    }

    private object? BuildAnswerOrNull() => BuildAnswer();

    private void RefreshCanSubmit()
        => CanSubmit = Questions.Count > 0 && Questions.All(question => question.IsAnswered);
}

/** 单个问题的选择状态: 单选/多选/自由输入。 */
public sealed partial class DecisionQuestionViewModel : ObservableObject
{
    public DecisionQuestionViewModel(AskUserQuestionItem question)
    {
        Item = question;
        Options = [.. (question.Options ?? []).Select(option => new DecisionOptionViewModel(option))];
        AllowCustom = question.Options is not { Count: > 0 };
        foreach (var option in Options)
            option.Changed += () => Notify();
    }

    public AskUserQuestionItem Item { get; }

    public ObservableCollection<DecisionOptionViewModel> Options { get; }

    public event Action? Changed;

    public string Question => Item.Question;

    public string Detail => Item.Detail ?? "";

    public bool HasDetail => Detail.Length > 0;

    public string Header => Item.Header ?? "";

    public bool HasHeader => Header.Length > 0;

    public bool MultiSelect => Item.MultiSelect;

    public bool AllowCustom { get; }

    public string ApproveLabel => Item.Intent?.Approve ?? "";

    [ObservableProperty]
    private string _customText = "";

    public bool IsAnswered => Options.Any(option => option.IsSelected) || CustomText.Trim().Length > 0;

    [RelayCommand]
    private void Select(DecisionOptionViewModel? option)
    {
        if (option is null)
            return;
        if (MultiSelect)
        {
            option.IsSelected = !option.IsSelected;
        }
        else
        {
            foreach (var candidate in Options)
                candidate.IsSelected = ReferenceEquals(candidate, option);
        }
        Notify();
    }

    public AskUserQuestionAnswerItem BuildAnswer()
    {
        var selected = Options.Where(option => option.IsSelected).Select(option => option.Label).ToList();
        var custom = CustomText.Trim();
        return new AskUserQuestionAnswerItem(Item.Id, selected, custom.Length > 0 ? custom : null);
    }

    private void Notify() => Changed?.Invoke();

    partial void OnCustomTextChanged(string value) => Notify();
}

public sealed partial class DecisionOptionViewModel : ObservableObject
{
    public DecisionOptionViewModel(AskUserQuestionOption option)
    {
        Label = option.Label;
        Description = option.Description ?? "";
    }

    public string Label { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public event Action? Changed;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => Changed?.Invoke();
}
