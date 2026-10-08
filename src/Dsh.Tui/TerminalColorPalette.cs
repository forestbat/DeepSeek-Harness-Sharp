namespace Dsh.Tui;

public readonly record struct Rgba(float R, float G, float B, float A = 1f);

public static class TerminalColorPalette
{
    public static readonly Rgba DefaultBackground = new(0f, 0f, 0f);
    public static readonly Rgba DefaultForeground = new(0.9f, 0.9f, 0.9f);

    public static Rgba ToRgba(CellColor color)
        => color.IsRgb
            ? new Rgba(color.R / 255f, color.G / 255f, color.B / 255f)
            : ToRgba(color.IsPalette ? color.PaletteColor : AnsiColor.Default);

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
