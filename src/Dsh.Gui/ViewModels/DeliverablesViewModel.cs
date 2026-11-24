using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Core;
using Dsh.Interaction;

namespace Dsh.Gui.ViewModels;

/** One presented file; Exists is read when the list is rebuilt so a deleted file shows as missing. */
public sealed class DeliverableItemViewModel(PresentedFile file)
{
    public string Path { get; } = file.Path;

    public string? Description { get; } = file.Description;

    public string FileName { get; } = System.IO.Path.GetFileName(file.Path);

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool Exists { get; } = File.Exists(file.Path);
}

/**
 * Header list of the shown session's deliverables.
 * It keeps the observed presentations and folds them with SessionDeliverables, so live appends, truncation, remote replay and the projection cache share one rule.
 */
public sealed partial class DeliverablesViewModel : ObservableObject
{
    /** 每次"交付"(present 调用)的文件组, 带事件序号: 截断按序号删。 */
    private readonly List<(long Seq, IReadOnlyList<PresentedFile> Files)> _presentations = [];

    public ObservableCollection<DeliverableItemViewModel> Items { get; } = [];

    /** False for remote sessions: their files live on the remote machine and cannot be opened locally. */
    [ObservableProperty]
    private bool _canOpen = true;

    public bool HasItems => Items.Count > 0;

    public string Badge => Items.Count.ToString();

    public void Reset(bool canOpen)
    {
        _presentations.Clear();
        CanOpen = canOpen;
        Rebuild();
    }

    /** 从投影缓存恢复: 存的是每次交付的文件组, 折叠仍走同一套规则。 */
    public void Restore(IReadOnlyList<(long Seq, IReadOnlyList<PresentedFile> Files)> presentations, bool canOpen)
    {
        _presentations.Clear();
        _presentations.AddRange(presentations);
        CanOpen = canOpen;
        Rebuild();
    }

    public void Observe(SessionEvent sessionEvent)
    {
        if (sessionEvent.Data is not DeliverablesPresentedPayload presented)
            return;
        _presentations.Add((sessionEvent.Seq, presented.Files));
        Rebuild();
    }

    public void TruncateTo(long eventCount)
    {
        if (_presentations.RemoveAll(presentation => presentation.Seq >= eventCount) > 0)
            Rebuild();
    }

    /** 缓存要存下来的形态: 每次交付的序号与文件组。 */
    public IReadOnlyList<(long Seq, IReadOnlyList<PresentedFile> Files)> Presentations => _presentations;

    private void Rebuild()
    {
        Items.Clear();
        foreach (var file in SessionDeliverables.Collect(_presentations.Select(presentation => presentation.Files)))
            Items.Add(new DeliverableItemViewModel(file));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Badge));
    }
}