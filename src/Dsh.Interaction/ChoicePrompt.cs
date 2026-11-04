namespace Dsh.Interaction;

public sealed record ChoicePromptOption(string Label, string? Description, string Value, bool Disabled = false, bool Default = false);

public sealed record ChoicePrompt(
    string Title,
    string? Description,
    IReadOnlyList<ChoicePromptOption> Options,
    bool MultiSelect = false,
    bool AllowCustom = true,
    string? RequestId = null)
{
    public static ChoicePrompt FromQuestion(AskUserQuestionItem question, string? requestId = null)
        => new(question.Header ?? question.Question,
            question.Header is null ? question.Detail : question.Question + "\n" + question.Detail,
            [.. (question.Options ?? []).Select(option => new ChoicePromptOption(option.Label, option.Description, option.Value ?? option.Label, option.Disabled, option.Default))],
            question.MultiSelect, question.AllowCustom, requestId);

    public static ChoicePrompt ForApproval(string toolName, string? arguments, string? reason, string? requestId = null, bool allowOnce = true, bool allowForSession = true, bool reject = true)
        => new($"Approve tool {toolName}",
            string.Join("\n", new[] { reason, ApprovalHints.PrimaryArgument(toolName, arguments), ApprovalHints.Impact(toolName, arguments), arguments }.Where(value => !string.IsNullOrWhiteSpace(value))),
            [new("Allow once", allowOnce ? "Allow only this invocation (y)" : "One-time allowance is unavailable for this request", "once", Disabled: !allowOnce),
             new("Always", allowForSession ? "Allow this tool in the current session only (a)" : "Session-scoped allowance is unavailable for this request", "session", Disabled: !allowForSession),
             new("Reject", reject ? "Deny this invocation (n)" : "One-time rejection is unavailable for this request", "reject", Disabled: !reject),
             new("Cancel", "Dismiss this approval without allowing the tool (c / Esc)", "cancel")],
            AllowCustom: false, RequestId: requestId);
}

public sealed class ChoicePromptState
{
    private readonly HashSet<int> _selected = [];

    public ChoicePromptState(ChoicePrompt prompt)
    {
        Prompt = prompt;
        FocusedIndex = -1;
        for (var index = 0; index < prompt.Options.Count; index++)
        {
            var option = prompt.Options[index];
            if (option.Disabled)
                continue;
            if (FocusedIndex < 0)
                FocusedIndex = index;
            if (option.Default && (prompt.MultiSelect || _selected.Count == 0))
            {
                _selected.Add(index);
                FocusedIndex = index;
            }
        }
        CustomFocused = FocusedIndex < 0 && prompt.AllowCustom;
    }

    public ChoicePrompt Prompt { get; }
    public int FocusedIndex { get; private set; }
    public bool CustomFocused { get; private set; }
    public int CustomCursor { get; set; }
    public string CustomText
    {
        get;
        set
        {
            field = Prompt.AllowCustom ? value : "";
            CustomCursor = Math.Clamp(CustomCursor, 0, field.Length);
        }
    } = "";
    public bool CanSubmit => _selected.Count > 0 || Prompt.AllowCustom && !string.IsNullOrWhiteSpace(CustomText);
    public bool IsSelected(int index) => _selected.Contains(index);

    public void Focus(int index)
    {
        if (index < 0 || index >= Prompt.Options.Count || Prompt.Options[index].Disabled)
            return;
        FocusedIndex = index;
        CustomFocused = false;
    }

    public void FocusCustom()
    {
        if (Prompt.AllowCustom)
            CustomFocused = true;
    }

    public void Move(int direction)
    {
        if (direction == 0)
            return;
        var index = CustomFocused ? (direction > 0 ? -1 : Prompt.Options.Count) : FocusedIndex;
        for (index += Math.Sign(direction); index >= 0 && index < Prompt.Options.Count; index += Math.Sign(direction))
        {
            if (Prompt.Options[index].Disabled)
                continue;
            Focus(index);
            return;
        }
    }

    public void Home() => FocusBoundary(false);
    public void End() => FocusBoundary(true);

    private void FocusBoundary(bool last)
    {
        for (var index = last ? Prompt.Options.Count - 1 : 0; index >= 0 && index < Prompt.Options.Count; index += last ? -1 : 1)
        {
            if (Prompt.Options[index].Disabled)
                continue;
            Focus(index);
            return;
        }
    }

    public void Select(int index)
    {
        if (index < 0 || index >= Prompt.Options.Count || Prompt.Options[index].Disabled)
            return;
        Focus(index);
        if (!Prompt.MultiSelect)
        {
            _selected.Clear();
            _selected.Add(index);
        }
        else if (!_selected.Remove(index))
            _selected.Add(index);
    }

    public IReadOnlyList<string> SelectedValues()
        => [.. _selected.Order().Select(index => Prompt.Options[index].Value)];

    public AskUserQuestionAnswerItem BuildAnswer(string id)
    {
        var custom = Prompt.AllowCustom ? CustomText.Trim() : "";
        return new(id, SelectedValues(), custom.Length == 0 ? null : custom);
    }
}
