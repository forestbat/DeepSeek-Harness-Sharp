using System.Text;
using Dsh.Interaction;

namespace Dsh.Tui;

public sealed class ChoicePromptControl
{
    private readonly List<PromptRow> _rows = [];
    private ConsoleRect _bounds;
    private int _layoutWidth = -1;
    private int _scroll;
    private int _viewportHeight;
    private int _footerFocus;
    private bool _ensureFocus = true;
    private bool _completed;

    private sealed record PromptRow(string Text, int Option = -1, int MarkerColumn = -1);

    public ChoicePromptControl(ChoicePrompt prompt)
    {
        State = new ChoicePromptState(prompt);
        if (State.CustomFocused)
            _footerFocus = 1;
    }

    public ChoicePromptState State { get; }
    public int ScrollOffset => _scroll;
    public ConsoleRect Bounds => _bounds;
    public event Action<bool>? Completed;

    public void HandleKey(ConsoleKeyInfo key)
    {
        if (_completed)
            return;
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                Complete(true);
                return;
            case ConsoleKey.Tab:
                var direction = (key.Modifiers & ConsoleModifiers.Shift) == 0 ? 1 : -1;
                do
                    _footerFocus = (_footerFocus + direction + 4) % 4;
                while (_footerFocus == 1 && !State.Prompt.AllowCustom || _footerFocus == 0 && State.FocusedIndex < 0);
                if (_footerFocus == 1)
                    State.FocusCustom();
                else if (_footerFocus == 0)
                    State.Focus(State.FocusedIndex);
                _ensureFocus = true;
                return;
            case ConsoleKey.UpArrow:
            case ConsoleKey.DownArrow:
            case ConsoleKey.LeftArrow when _footerFocus != 1:
            case ConsoleKey.RightArrow when _footerFocus != 1:
                if (State.FocusedIndex < 0)
                    return;
                _footerFocus = 0;
                State.Move(key.Key is ConsoleKey.UpArrow or ConsoleKey.LeftArrow ? -1 : 1);
                _ensureFocus = true;
                return;
            case ConsoleKey.Home when _footerFocus != 1:
                State.Home();
                _footerFocus = 0;
                _ensureFocus = true;
                return;
            case ConsoleKey.End when _footerFocus != 1:
                State.End();
                _footerFocus = 0;
                _ensureFocus = true;
                return;
            case ConsoleKey.PageUp:
                Scroll(-Math.Max(1, _viewportHeight));
                return;
            case ConsoleKey.PageDown:
                Scroll(Math.Max(1, _viewportHeight));
                return;
            case ConsoleKey.Enter:
                if (_footerFocus == 3)
                    Complete(true);
                else
                {
                    if (_footerFocus == 0 && !State.Prompt.MultiSelect)
                        State.Select(State.FocusedIndex);
                    if (State.CanSubmit)
                        Complete(false);
                }
                return;
            case ConsoleKey.Spacebar when _footerFocus == 0:
                State.Select(State.FocusedIndex);
                return;
        }
        if (_footerFocus != 1 && key.KeyChar is >= '1' and <= '9')
        {
            State.Select(key.KeyChar - '1');
            _footerFocus = 0;
            _ensureFocus = true;
            return;
        }
        if (_footerFocus != 1)
            return;
        var cursor = Math.Clamp(State.CustomCursor, 0, State.CustomText.Length);
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:
                State.CustomCursor = PreviousCharacter(State.CustomText, cursor);
                break;
            case ConsoleKey.RightArrow:
                State.CustomCursor = NextCharacter(State.CustomText, cursor);
                break;
            case ConsoleKey.Home:
                State.CustomCursor = 0;
                break;
            case ConsoleKey.End:
                State.CustomCursor = State.CustomText.Length;
                break;
            case ConsoleKey.Backspace when cursor > 0:
                var previous = PreviousCharacter(State.CustomText, cursor);
                State.CustomText = State.CustomText.Remove(previous, cursor - previous);
                State.CustomCursor = previous;
                break;
            case ConsoleKey.Delete when cursor < State.CustomText.Length:
                State.CustomText = State.CustomText.Remove(cursor, NextCharacter(State.CustomText, cursor) - cursor);
                break;
            default:
                if (!char.IsControl(key.KeyChar))
                    InsertText(key.KeyChar.ToString());
                break;
        }
    }

    private static int PreviousCharacter(string text, int cursor)
        => cursor <= 0 ? 0 : cursor > 1 && char.IsLowSurrogate(text[cursor - 1]) && char.IsHighSurrogate(text[cursor - 2]) ? cursor - 2 : cursor - 1;

    private static int NextCharacter(string text, int cursor)
        => cursor >= text.Length ? text.Length : cursor + 1 < text.Length && char.IsHighSurrogate(text[cursor]) && char.IsLowSurrogate(text[cursor + 1]) ? cursor + 2 : cursor + 1;

    public void InsertText(string text)
    {
        if (_completed || !State.Prompt.AllowCustom)
            return;
        _footerFocus = 1;
        State.FocusCustom();
        var sanitized = text.Replace('\r', ' ').Replace('\n', ' ');
        var cursor = Math.Clamp(State.CustomCursor, 0, State.CustomText.Length);
        State.CustomText = State.CustomText.Insert(cursor, sanitized);
        State.CustomCursor = cursor + sanitized.Length;
    }

    public void Scroll(int lines)
    {
        _scroll = (int)Math.Clamp((long)_scroll + lines, 0L, Math.Max(0, _rows.Count - _viewportHeight));
        _ensureFocus = false;
    }

    public bool HandlePointer(int x, int y, bool click)
    {
        if (_completed || !_bounds.Contains(x, y))
            return false;
        var oldFocus = State.FocusedIndex;
        var oldFooter = _footerFocus;
        var row = y - _bounds.Y - 1;
        if (row >= 0 && row < _viewportHeight)
        {
            var index = row + _scroll;
            if (index < _rows.Count && _rows[index].Option >= 0 && x < _bounds.X + TerminalTextWidth.Of(_rows[index].Text))
            {
                var option = _rows[index].Option;
                State.Focus(option);
                if (!State.Prompt.Options[option].Disabled)
                    _footerFocus = 0;
                if (click)
                    State.Select(option);
            }
            return oldFocus != State.FocusedIndex || oldFooter != _footerFocus || click;
        }
        if (y == _bounds.Bottom - 3 && State.Prompt.AllowCustom)
        {
            _footerFocus = 1;
            State.FocusCustom();
            if (click)
                State.CustomCursor = State.CustomText.Length;
        }
        else if (y == _bounds.Bottom - 2 && x < _bounds.X + 10)
        {
            _footerFocus = 2;
            if (click && State.CanSubmit)
                Complete(false);
        }
        else if (y == _bounds.Bottom - 1 && x < _bounds.X + 10)
        {
            _footerFocus = 3;
            if (click)
                Complete(true);
        }
        return oldFocus != State.FocusedIndex || oldFooter != _footerFocus || click;
    }

    public void Draw(CellGrid grid, ConsoleRect area)
    {
        _bounds = area;
        if (area.Width < 1 || area.Height < 1)
            return;
        var width = area.Width;
        if (_layoutWidth != width)
        {
            _rows.Clear();
            _layoutWidth = width;
            _ensureFocus = true;
            AddWrapped(State.Prompt.Title, width);
            if (State.Prompt.Description is { Length: > 0 } description)
                AddWrapped(description, width);
            for (var index = 0; index < State.Prompt.Options.Count; index++)
            {
                var option = State.Prompt.Options[index];
                var prefix = index < 9 ? $"{index + 1}. " : "   ";
                AddWrapped($"{prefix}[ ] {option.Label}" + (option.Disabled ? " (disabled)" : "") + (string.IsNullOrEmpty(option.Description) ? "" : " - " + option.Description), width, index);
            }
        }
        var viewportHeight = Math.Max(0, area.Height - 4);
        if (_viewportHeight != viewportHeight)
            _ensureFocus = true;
        _viewportHeight = viewportHeight;
        if (_ensureFocus && _footerFocus == 0)
        {
            var focusedRow = _rows.FindIndex(row => row.Option == State.FocusedIndex);
            if (focusedRow >= 0 && _viewportHeight > 0)
            {
                if (focusedRow < _scroll)
                    _scroll = focusedRow;
                else if (focusedRow >= _scroll + _viewportHeight)
                    _scroll = focusedRow - _viewportHeight + 1;
            }
        }
        _ensureFocus = false;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _rows.Count - _viewportHeight));
        for (var y = area.Y; y < area.Bottom; y++)
            for (var x = area.X; x < area.Right; x++)
                if (x >= 0 && y >= 0 && x < grid.Width && y < grid.Height)
                    grid[x, y] = new Cell(' ', AnsiColor.Default, AnsiColor.Default);
        DrawLine(grid, area.Y, "Choice: arrows/Home/End  Space  1-9  Tab  Enter  Esc", CellStyle.Bold);
        for (var row = 0; row < _viewportHeight && row + _scroll < _rows.Count; row++)
        {
            var content = _rows[row + _scroll];
            var disabled = content.Option >= 0 && State.Prompt.Options[content.Option].Disabled;
            var focused = content.Option >= 0 && content.Option == State.FocusedIndex && _footerFocus == 0;
            DrawLine(grid, area.Y + row + 1, content.Text, disabled ? CellStyle.Dim : focused ? CellStyle.Reverse : CellStyle.None);
            var markerX = area.X + content.MarkerColumn;
            var markerY = area.Y + row + 1;
            if (content.MarkerColumn >= 0 && State.IsSelected(content.Option) && markerX >= 0 && markerX < grid.Width && markerY >= 0 && markerY < grid.Height)
                grid[markerX, markerY] = grid[markerX, markerY] with { Character = 'x' };
        }
        if (State.Prompt.AllowCustom)
        {
            var cursor = Math.Clamp(State.CustomCursor, 0, State.CustomText.Length);
            var start = cursor;
            var columns = 0;
            while (start > 0 && columns + TerminalTextWidth.Of(State.CustomText[start - 1]) < Math.Max(1, width - 10))
                columns += TerminalTextWidth.Of(State.CustomText[--start]);
            DrawLine(grid, area.Bottom - 3, "Custom: " + State.CustomText[start..], _footerFocus == 1 ? CellStyle.Reverse : CellStyle.None);
            var caretX = area.X + 8 + TerminalTextWidth.Of(State.CustomText.AsSpan(start, cursor - start));
            if (_footerFocus == 1 && caretX >= area.X && caretX < area.Right && area.Bottom - 3 >= 0 && area.Bottom - 3 < grid.Height)
                grid[caretX, area.Bottom - 3] = grid[caretX, area.Bottom - 3] with { Style = CellStyle.Reverse | CellStyle.Bold };
        }
        DrawLine(grid, area.Bottom - 2, State.CanSubmit ? "[ Submit ]" : "[ Submit: choose an option or enter an answer ]", _footerFocus == 2 ? CellStyle.Reverse : CellStyle.Bold);
        DrawLine(grid, area.Bottom - 1, "[ Cancel ]", _footerFocus == 3 ? CellStyle.Reverse : CellStyle.None);
    }

    private void DrawLine(CellGrid grid, int y, string text, CellStyle style)
    {
        if (y < _bounds.Y || y >= _bounds.Bottom)
            return;
        var length = 0;
        var columns = 0;
        while (length < text.Length)
        {
            var width = TerminalTextWidth.Of(text[length]);
            if (columns + width > _bounds.Width)
                break;
            columns += width;
            length++;
        }
        CellText.Draw(grid, _bounds.X, y, text.AsSpan(0, length), AnsiColor.Default, AnsiColor.Default, style);
    }

    private void AddWrapped(string text, int width, int option = -1)
    {
        var firstLine = true;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            var buffer = new StringBuilder();
            var columns = 0;
            var position = 0;
            var markerColumn = -1;
            foreach (var character in line)
            {
                if (char.IsControl(character))
                    continue;
                var characterWidth = TerminalTextWidth.Of(character);
                if (columns + characterWidth > width && buffer.Length > 0)
                {
                    _rows.Add(new(buffer.ToString(), option, markerColumn));
                    buffer.Clear();
                    columns = 0;
                    markerColumn = -1;
                }
                if (firstLine && option >= 0 && position == 4)
                    markerColumn = columns;
                position++;
                buffer.Append(character);
                columns += characterWidth;
            }
            _rows.Add(new(buffer.ToString(), option, markerColumn));
            firstLine = false;
        }
    }

    private void Complete(bool dismissed)
    {
        if (_completed)
            return;
        _completed = true;
        Completed?.Invoke(dismissed);
    }
}
