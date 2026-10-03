namespace Dsh.Pty;

[Flags]
public enum CellStyle : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Reverse = 4,
}

public enum AnsiColor : byte
{
    Default = 0,
    Black = 1,
    Red = 2,
    Green = 3,
    Yellow = 4,
    Blue = 5,
    Magenta = 6,
    Cyan = 7,
    White = 8,
    BrightBlack = 9,
    BrightRed = 10,
    BrightGreen = 11,
    BrightYellow = 12,
    BrightBlue = 13,
    BrightMagenta = 14,
    BrightCyan = 15,
    BrightWhite = 16,
}

public readonly record struct Cell(
    char Character = ' ',
    AnsiColor Foreground = AnsiColor.Default,
    AnsiColor Background = AnsiColor.Default,
    CellStyle Style = CellStyle.None);

public readonly record struct CellChange(int X, int Y, Cell Cell);

public sealed class CellGrid
{
    private readonly Cell[] _cells;

    public CellGrid(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "CellGrid dimensions must be positive.");
        Width = width;
        Height = height;
        _cells = new Cell[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    internal Cell[] RawCells => _cells;

    public ReadOnlySpan<Cell> Cells => _cells;

    public ReadOnlySpan<Cell> Row(int y)
    {
        if ((uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(y));
        return _cells.AsSpan(y * Width, Width);
    }

    public void SetRow(int y, ReadOnlySpan<Cell> row)
    {
        if ((uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(y));
        var target = _cells.AsSpan(y * Width, Width);
        if (row.Length >= Width)
            row[..Width].CopyTo(target);
        else
        {
            row.CopyTo(target);
            target[row.Length..].Clear();
        }
    }

    public Cell this[int x, int y]
    {
        get => _cells[(y * Width) + x];
        set
        {
            var index = (y * Width) + x;
            var previous = _cells[index];
            _cells[index] = value;
            RepairWideCharSplit(x, y, previous, value);
        }
    }

    /** 覆盖写不能留下悬空半宽字符: 占位格被覆盖则清左侧宽字符本体, 宽字符本体被覆盖则清右侧占位格。 */
    private void RepairWideCharSplit(int x, int y, Cell previous, Cell value)
    {
        if (previous.Character == '\0' && value.Character != '\0' && x > 0)
        {
            var leftIndex = (y * Width) + x - 1;
            var left = _cells[leftIndex];
            if (TerminalTextWidth.Of(left.Character) == 2)
                _cells[leftIndex] = left with { Character = ' ' };
        }
        if (TerminalTextWidth.Of(previous.Character) == 2
            && TerminalTextWidth.Of(value.Character) != 2
            && x + 1 < Width
            && _cells[(y * Width) + x + 1].Character == '\0')
        {
            var rightIndex = (y * Width) + x + 1;
            _cells[rightIndex] = _cells[rightIndex] with { Character = ' ' };
        }
    }

    public void Clear(Cell cell = default)
        => Array.Fill(_cells, cell);

    public CellGrid Clone()
    {
        var clone = new CellGrid(Width, Height);
        Array.Copy(_cells, clone._cells, _cells.Length);
        return clone;
    }

    public void CopyTo(CellGrid destination)
    {
        if (destination.Width != Width || destination.Height != Height)
            throw new ArgumentException("Destination grid must have the same dimensions.", nameof(destination));
        Array.Copy(_cells, destination._cells, _cells.Length);
    }

    public IEnumerable<CellChange> Diff(CellGrid previous)
    {
        if (previous.Width != Width || previous.Height != Height)
            throw new ArgumentException("Previous grid must have the same dimensions.", nameof(previous));

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = this[x, y];
                if (cell != previous[x, y])
                    yield return new CellChange(x, y, cell);
            }
        }
    }
}
