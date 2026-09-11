namespace Dsh.Tui;

[Flags]
public enum CellStyle
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Reverse = 4,
}

public enum AnsiColor
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

    public Cell this[int x, int y]
    {
        get => _cells[(y * Width) + x];
        set => _cells[(y * Width) + x] = value;
    }

    public void Clear(Cell cell = default)
        => Array.Fill(_cells, cell);

    public CellGrid Clone()
    {
        var clone = new CellGrid(Width, Height);
        Array.Copy(_cells, clone._cells, _cells.Length);
        return clone;
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