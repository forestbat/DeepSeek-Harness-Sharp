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

    /** 压住通知执行一批改动, 结束后按需补发一次 Reset。 */
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

    /**
     * 队首插入一批。
     * 用一次 Insert 通知而不是 Reset: Reset 会让虚拟化面板把整屏当成新数据重排,
     * 窗口里已有几十条、内容高几千像素时, 那一次重排要一秒。
     */
    public void Prefix(IReadOnlyList<MessageViewModel> items)
    {
        if (items.Count == 0)
            return;
        _depth += 1;
        try
        {
            for (var offset = 0; offset < items.Count; offset += 1)
                InsertItem(offset, items[offset]);
        }
        finally
        {
            _depth -= 1;
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, items.ToList(), 0));
    }

    /** 队尾追加一批, 只通知一次。 */
    public void Suffix(IReadOnlyList<MessageViewModel> items)
    {
        if (items.Count == 0)
            return;
        var start = Count;
        _depth += 1;
        try
        {
            foreach (var item in items)
                InsertItem(Count, item);
        }
        finally
        {
            _depth -= 1;
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, items.ToList(), start));
    }

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
