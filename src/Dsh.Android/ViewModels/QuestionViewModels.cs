using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.RemoteHost;

namespace Dsh.Android.ViewModels;

/** 问题里的一个选项; 单选时由所属问题负责取消其他选项。 */
public sealed partial class QuestionOptionViewModel(QuestionItemViewModel owner, string label, string? description) : ObservableObject
{
    public string Label { get; } = label;

    public string? Description { get; } = description;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            owner.DeselectOthers(this);
        owner.NotifyChanged();
    }
}

/** 一道待回答的问题; Custom 与选项二选一即可提交。 */
public sealed partial class QuestionItemViewModel : ObservableObject
{
    public QuestionItemViewModel(RemoteQuestionItem item)
    {
        Id = item.Id;
        Question = item.Question;
        Detail = item.Detail;
        MultiSelect = item.MultiSelect;
        Options = [.. item.Options.Select(option => new QuestionOptionViewModel(this, option.Label, option.Description))];
    }

    public event Action? Changed;

    public string Id { get; }

    public string Question { get; }

    public string? Detail { get; }

    public bool MultiSelect { get; }

    public bool HasDetail => Detail is { Length: > 0 };

    public ObservableCollection<QuestionOptionViewModel> Options { get; }

    [ObservableProperty]
    private string _custom = "";

    public IReadOnlyList<string> SelectedLabels => [.. Options.Where(option => option.IsSelected).Select(option => option.Label)];

    internal void DeselectOthers(QuestionOptionViewModel keep)
    {
        if (MultiSelect)
            return;
        foreach (var option in Options)
        {
            if (!ReferenceEquals(option, keep) && option.IsSelected)
                option.IsSelected = false;
        }
    }

    internal void NotifyChanged() => Changed?.Invoke();

    partial void OnCustomChanged(string value) => Changed?.Invoke();
}
