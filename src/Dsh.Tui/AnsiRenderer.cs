using System.Runtime.InteropServices;
using System.Text;

namespace Dsh.Tui;

/**
 * VT 帧渲染: 行级脏区 diff + 跨帧 SGR 样式状态机(连续同样式零 SGR, 变化只发差异属性),
 * 整帧拼一个缓冲由调用方一次写入, 外面包 ?2026h/l 同步输出(不支持的宿主按规范忽略)。
 * 状态机跨帧携带: 帧尾若处于非默认样式则补 0m, 终端物理样式始终与本机记录一致。
 */
public sealed class AnsiRenderer
{
    private const string SyncBegin = "\x1b[?2026h";
    private const string SyncEnd = "\x1b[?2026l";

    private readonly StringBuilder _builder = new();
    private char[] _outputBuffer = new char[64 * 1024];
    private CellGrid? _previous;
    private SgrState _sgr;
    private int _lastCursorX = -1;
    private int _lastCursorY = -1;

    public string Render(CellGrid grid, int cursorX, int cursorY, bool forceFull = false)
    {
        BuildFrame(grid, cursorX, cursorY, forceFull);
        return _builder.ToString();
    }

    public int Render(CellGrid grid, int cursorX, int cursorY, Span<char> destination, bool forceFull = false)
    {
        BuildFrame(grid, cursorX, cursorY, forceFull);
        var builder = _builder;
        if (destination.Length < builder.Length)
            throw new ArgumentException($"输出缓冲区需要至少 {builder.Length} 个字符", nameof(destination));
        builder.CopyTo(0, destination, builder.Length);
        return builder.Length;
    }

    public ReadOnlyMemory<char> RenderToBuffer(CellGrid grid, int cursorX, int cursorY, bool forceFull = false)
    {
        BuildFrame(grid, cursorX, cursorY, forceFull);
        var builder = _builder;
        if (_outputBuffer.Length < builder.Length)
            _outputBuffer = new char[Math.Max(builder.Length, _outputBuffer.Length * 2)];
        builder.CopyTo(0, _outputBuffer, builder.Length);
        return _outputBuffer.AsMemory(0, builder.Length);
    }

    public void Reset()
    {
        _previous = null;
        _sgr.Invalidate();
        _lastCursorX = -1;
        _lastCursorY = -1;
    }

    private void BuildFrame(CellGrid grid, int cursorX, int cursorY, bool forceFull)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var needsFull = forceFull
            || _previous is null
            || _previous.Width != grid.Width
            || _previous.Height != grid.Height;

        var builder = _builder;
        builder.Clear();
        builder.Append(SyncBegin);
        if (needsFull)
        {
            AppendFull(builder, grid);
            if (_previous is null || _previous.Width != grid.Width || _previous.Height != grid.Height)
                _previous = grid.Clone();
            else
                grid.CopyTo(_previous);
            // 2J 把光标归位, 缓存的光标位置作废
            _lastCursorX = -1;
            _lastCursorY = -1;
        }
        else
        {
            AppendDiff(builder, grid, _previous!);
        }

        _sgr.Reset(builder);
        AppendCursor(builder, cursorX, cursorY, grid.Width, grid.Height);
        builder.Append(SyncEnd);
    }

    private void AppendFull(StringBuilder builder, CellGrid grid)
    {
        builder.Append("\x1b[2J\x1b[3J\x1b[H");
        for (var y = 0; y < grid.Height; y++)
        {
            var row = grid.Row(y);
            for (var x = 0; x < row.Length; x++)
            {
                var cell = row[x];
                // 宽字符续格占位(紧跟在宽字符后的 '\0')不单独输出: 宽字符已让光标推进两格;
                // 其余 '\0'(未初始化格)按空格输出以保持列位。与 AppendDiff 的判定一致。
                if (cell.Character == '\0' && x > 0 && TerminalTextWidth.IsWide(row[x - 1].Character))
                    continue;

                _sgr.Apply(builder, cell);
                builder.Append(Sanitize(cell.Character));
            }

            if (y < grid.Height - 1)
                builder.Append("\r\n");
        }
    }

    private void AppendDiff(StringBuilder builder, CellGrid grid, CellGrid previous)
    {
        for (var y = 0; y < grid.Height; y++)
        {
            var row = grid.Row(y);
            if (MemoryMarshal.Cast<Cell, byte>(row).SequenceEqual(MemoryMarshal.Cast<Cell, byte>(previous.Row(y))))
                continue;
            AppendPosition(builder, 0, y);
            var x = 0;
            while (x < row.Length)
            {
                var cell = row[x];
                if (cell.Character == '\0' && x > 0 && TerminalTextWidth.IsWide(row[x - 1].Character))
                {
                    x++;
                    continue;
                }

                _sgr.Apply(builder, cell);
                builder.Append(Sanitize(cell.Character));
                x++;
            }
            row.CopyTo(previous.RawCells.AsSpan(y * row.Length, row.Length));
        }
    }

    private static void AppendPosition(StringBuilder builder, int x, int y)
    {
        builder.Append("\x1b[");
        builder.Append(y + 1);
        builder.Append(';');
        builder.Append(x + 1);
        builder.Append('H');
    }

    private void AppendCursor(StringBuilder builder, int cursorX, int cursorY, int width, int height)
    {
        cursorX = Math.Clamp(cursorX, 0, width - 1);
        cursorY = Math.Clamp(cursorY, 0, height - 1);
        if (cursorX == _lastCursorX && cursorY == _lastCursorY)
            return;
        AppendPosition(builder, cursorX, cursorY);
        _lastCursorX = cursorX;
        _lastCursorY = cursorY;
    }

    private static string ForegroundSegment(CellColor color)
        => color.IsRgb ? $"38;2;{color.R};{color.G};{color.B}" : ForegroundCode(color.IsPalette ? color.PaletteColor : AnsiColor.Default).ToString();

    private static string BackgroundSegment(CellColor color)
        => color.IsRgb ? $"48;2;{color.R};{color.G};{color.B}" : BackgroundCode(color.IsPalette ? color.PaletteColor : AnsiColor.Default).ToString();

    private static int ForegroundCode(AnsiColor color)
    {
        if (color == AnsiColor.Default)
            return 39;
        if (color >= AnsiColor.BrightBlack)
            return 90 + (color - AnsiColor.BrightBlack);
        return 30 + (color - AnsiColor.Black);
    }

    private static int BackgroundCode(AnsiColor color)
    {
        if (color == AnsiColor.Default)
            return 49;
        if (color >= AnsiColor.BrightBlack)
            return 100 + (color - AnsiColor.BrightBlack);
        return 40 + (color - AnsiColor.Black);
    }

    private static char Sanitize(char value)
        => value == '\0' ? ' ' : TerminalSafeGlyphs.AsciiSafe(value);

    /** 终端当前 SGR 状态的本机记录; Apply 只发与当前态的差异属性。 */
    private struct SgrState
    {
        private CellColor _foreground;
        private CellColor _background;
        private CellStyle _style;
        private bool _valid;

        public void Invalidate() => _valid = false;

        public void Apply(StringBuilder builder, Cell cell)
        {
            if (_valid && _foreground == cell.Foreground && _background == cell.Background && _style == cell.Style)
                return;
            if (_valid)
                AppendDelta(builder, cell);
            else
                AppendFull(builder, cell);
            _foreground = cell.Foreground;
            _background = cell.Background;
            _style = cell.Style;
            _valid = true;
        }

        /** 帧尾归零: 非默认样式补 0m, 归零后状态记为有效的默认态(下一帧默认样式零 SGR)。 */
        public void Reset(StringBuilder builder)
        {
            if (_valid && (_foreground != AnsiColor.Default || _background != AnsiColor.Default || _style != CellStyle.None))
                builder.Append("\x1b[0m");
            _foreground = AnsiColor.Default;
            _background = AnsiColor.Default;
            _style = CellStyle.None;
            _valid = true;
        }

        private void AppendFull(StringBuilder builder, Cell cell)
        {
            builder.Append("\x1b[");
            var separator = false;
            AppendStyleOn(builder, cell.Style, ref separator);
            AppendSeparator(builder, ref separator);
            builder.Append(ForegroundSegment(cell.Foreground));
            builder.Append(';');
            builder.Append(BackgroundSegment(cell.Background));
            builder.Append('m');
        }

        private void AppendDelta(StringBuilder builder, Cell cell)
        {
            builder.Append("\x1b[");
            var separator = false;
            var emitted22 = false;
            var toggled = _style ^ cell.Style;
            AppendInt(builder, (toggled & CellStyle.Bold) != 0 ? (cell.Style & CellStyle.Bold) != 0 ? 1 : 22 : null, ref separator, ref emitted22);
            AppendInt(builder, (toggled & CellStyle.Dim) != 0 ? (cell.Style & CellStyle.Dim) != 0 ? 2 : 22 : null, ref separator, ref emitted22);
            AppendInt(builder, (toggled & CellStyle.Reverse) != 0 ? (cell.Style & CellStyle.Reverse) != 0 ? 7 : 27 : null, ref separator, ref emitted22);
            if (_foreground != cell.Foreground)
            {
                AppendSeparator(builder, ref separator);
                builder.Append(ForegroundSegment(cell.Foreground));
            }
            if (_background != cell.Background)
            {
                AppendSeparator(builder, ref separator);
                builder.Append(BackgroundSegment(cell.Background));
            }
            builder.Append('m');
        }

        private static void AppendInt(StringBuilder builder, int? code, ref bool separator, ref bool emitted22)
        {
            if (code is not { } value)
                return;
            if (value == 22)
            {
                if (emitted22)
                    return;
                emitted22 = true;
            }
            AppendSeparator(builder, ref separator);
            builder.Append(value);
        }

        private static void AppendSeparator(StringBuilder builder, ref bool separator)
        {
            if (separator)
                builder.Append(';');
            separator = true;
        }

        private static void AppendStyleOn(StringBuilder builder, CellStyle style, ref bool separator)
        {
            AppendBit(builder, style, CellStyle.Bold, '1', ref separator);
            AppendBit(builder, style, CellStyle.Dim, '2', ref separator);
            AppendBit(builder, style, CellStyle.Reverse, '7', ref separator);
        }

        private static void AppendBit(StringBuilder builder, CellStyle style, CellStyle bit, char code, ref bool separator)
        {
            if ((style & bit) == 0)
                return;
            if (separator)
                builder.Append(';');
            builder.Append(code);
            separator = true;
        }
    }
}
