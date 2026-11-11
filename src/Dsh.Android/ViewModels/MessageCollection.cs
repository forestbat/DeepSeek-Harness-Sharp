using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Dsh.Gui.ViewModels;

namespace Dsh.Android.ViewModels;

/**
 * 聊天消息集合: 批量改完只通知一次。
 * 逐条 Add/Insert 会让虚拟化列表每加一条就重算一次索引映射, 一次接上百条就是上百次重排,
 * 用户看到的正是"滑到头之后要等一会儿"。
 */
public sealed class MessageCollection : ObservableCollection<MessageViewModel>
{
    private int _depth;

    /** 压住通知执行一批改动, 结束后补发一次 Reset。 */
    public void Batch(Action changes)
    {
        _depth += 1;
        try
        {
            changes();
        }
        finally
        {
            _depth -= 1;
        }
        NotifyReset();
    }

    /** 队首插入一批, 只通知一次。 */
    public void Prefix(IReadOnlyList<MessageViewModel> items) => Batch(() =>
    {
        for (var offset = 0; offset < items.Count; offset += 1)
            InsertItem(offset, items[offset]);
    });

    /** 队尾追加一批, 只通知一次。 */
    public void Suffix(IReadOnlyList<MessageViewModel> items) => Batch(() =>
    {
        foreach (var item in items)
            InsertItem(Count, item);
    });

    private void NotifyReset()
    {
        if (_depth > 0)
            return;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /** 批量区间里的每条改动都不单独通知, 由 Batch 统一收口。 */
    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_depth > 0)
            return;
        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_depth > 0)
            return;
        base.OnPropertyChanged(e);
    }
}
