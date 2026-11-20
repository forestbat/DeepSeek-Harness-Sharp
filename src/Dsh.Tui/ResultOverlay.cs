namespace Dsh.Tui;

/**
 * 命令结果的只读浮层。
 * 非模态: 只吃导航键(Esc 也算), 其余按键照常进输入框, 所以用户可以一边看结果一边敲提示词。
 */
internal sealed class ResultOverlay
{
    private readonly List<string> _lines = [];
    private int _first;

    public bool IsActive { get; private set; }

    public bool IsError { get; private set; }

    public string Title { get; private set; } = "";

    public string Text { get; private set; } = "";

    public IReadOnlyList<string> Lines => _lines;

    public int VisibleRows { get; set; } = PopupList.PageRows;

    /** 当前视口首行: 交给 PopupList 当选中行, 复用它的居中与滚动条计算。 */
    public int First => _first;

    public void Show(string title, string text, bool isError)
    {
        Title = title;
        Text = text;
        IsError = isError;
        _lines.Clear();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            _lines.Add(line.Length == 0 ? " " : line);
        _first = 0;
        IsActive = true;
    }

    public void Close()
    {
        IsActive = false;
        _lines.Clear();
        Text = "";
    }

    public void MoveUp(int rows = 1) => _first = Math.Max(0, _first - Math.Max(1, rows));

    public void MoveDown(int rows = 1)
        => _first = Math.Min(MaxFirst, _first + Math.Max(1, rows));

    public void MovePage(int direction)
        => _first = Math.Clamp(_first + (Math.Max(1, VisibleRows) * direction), 0, MaxFirst);

    public void MoveHome() => _first = 0;

    public void MoveEnd() => _first = MaxFirst;

    private int MaxFirst => Math.Max(0, _lines.Count - Math.Max(1, VisibleRows));

    /**
     * 返回 true 表示这个键被浮层消费。
     * 只消费导航键与 Esc; 可打印字符必须返回 false, 让它们落到输入框(非模态)。
     */
    public bool HandleKey(ConsoleKeyInfo key)
    {
        if (!IsActive)
            return false;
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                Close();
                return true;
            case ConsoleKey.UpArrow:
                MoveUp();
                return true;
            case ConsoleKey.DownArrow:
                MoveDown();
                return true;
            case ConsoleKey.PageUp:
                MovePage(-1);
                return true;
            case ConsoleKey.PageDown:
                MovePage(1);
                return true;
            case ConsoleKey.Home:
                MoveHome();
                return true;
            case ConsoleKey.End:
                MoveEnd();
                return true;
            default:
                return false;
        }
    }
}
