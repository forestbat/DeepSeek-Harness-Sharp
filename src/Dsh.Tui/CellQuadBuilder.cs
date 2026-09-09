namespace Dsh.Tui;

public readonly record struct Rgba(float R, float G, float B, float A = 1f);

public readonly record struct CellQuad(
    float X,
    float Y,
    float Width,
    float Height,
    Rgba Color,
    float U0,
    float V0,
    float U1,
    float V1,
    bool IsGlyph);

public static class TerminalColorPalette
{
    public static readonly Rgba DefaultBackground = new(0.08f, 0.08f, 0.11f);
    public static readonly Rgba DefaultForeground = new(0.9f, 0.9f, 0.9f);

    public static Rgba ToRgba(AnsiColor color) => color switch
    {
        AnsiColor.Default => DefaultForeground,
        AnsiColor.Black => new Rgba(0.0f, 0.0f, 0.0f),
        AnsiColor.Red => new Rgba(0.80f, 0.19f, 0.19f),
        AnsiColor.Green => new Rgba(0.05f, 0.74f, 0.47f),
        AnsiColor.Yellow => new Rgba(0.90f, 0.90f, 0.06f),
        AnsiColor.Blue => new Rgba(0.14f, 0.45f, 0.78f),
        AnsiColor.Magenta => new Rgba(0.74f, 0.25f, 0.74f),
        AnsiColor.Cyan => new Rgba(0.07f, 0.66f, 0.80f),
        AnsiColor.White => new Rgba(0.90f, 0.90f, 0.90f),
        AnsiColor.BrightBlack => new Rgba(0.40f, 0.40f, 0.40f),
        AnsiColor.BrightRed => new Rgba(0.95f, 0.30f, 0.30f),
        AnsiColor.BrightGreen => new Rgba(0.14f, 0.82f, 0.55f),
        AnsiColor.BrightYellow => new Rgba(0.96f, 0.96f, 0.26f),
        AnsiColor.BrightBlue => new Rgba(0.23f, 0.56f, 0.92f),
        AnsiColor.BrightMagenta => new Rgba(0.84f, 0.44f, 0.84f),
        AnsiColor.BrightCyan => new Rgba(0.16f, 0.72f, 0.86f),
        AnsiColor.BrightWhite => new Rgba(1.0f, 1.0f, 1.0f),
        _ => DefaultForeground,
    };
}

public static class CellQuadBuilder
{
    private static readonly GlyphAtlas Atlas = new();

    public static IReadOnlyList<CellQuad> Build(CellGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var quads = new List<CellQuad>(grid.Width * grid.Height * 2);

        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
            {
                var cell = grid[x, y];
                var foreground = cell.Foreground;
                var background = cell.Background;
                if ((cell.Style & CellStyle.Reverse) != 0)
                    (foreground, background) = (background, foreground);

                quads.Add(new CellQuad(
                    x,
                    y,
                    1f,
                    1f,
                    background == AnsiColor.Default ? TerminalColorPalette.DefaultBackground : TerminalColorPalette.ToRgba(background),
                    0f,
                    0f,
                    0f,
                    0f,
                    false));

                var character = cell.Character == '\0' ? ' ' : cell.Character;
                if (character == ' ')
                    continue;

                var uv = Atlas.GetUv(character);
                var color = foreground == AnsiColor.Default ? TerminalColorPalette.DefaultForeground : TerminalColorPalette.ToRgba(foreground);
                if ((cell.Style & CellStyle.Dim) != 0)
                    color = color with { R = color.R * 0.5f, G = color.G * 0.5f, B = color.B * 0.5f };

                quads.Add(new CellQuad(
                    x,
                    y,
                    1f,
                    1f,
                    color,
                    uv.MinX,
                    uv.MinY,
                    uv.MaxX,
                    uv.MaxY,
                    true));
            }
        }

        return quads;
    }
}
