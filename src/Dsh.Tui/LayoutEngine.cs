namespace Dsh.Tui;

public readonly record struct ConsoleRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public readonly record struct UiLayout(
    ConsoleRect Main,
    ConsoleRect RightPanel,
    ConsoleRect Input,
    ConsoleRect Status);

public static class LayoutEngine
{
    public const int MaximumRightPanelWidth = 40;
    public const int InputHeight = 2;

    private const double RightPanelRatio = 0.32;
    private const int StatusHeight = 1;
    private const int DividerRows = 2;

    public static UiLayout Calculate(int consoleWidth, int consoleHeight)
    {
        var width = Math.Max(1, consoleWidth);
        var height = Math.Max(1, consoleHeight);

        var rightPanelWidth = Math.Min(MaximumRightPanelWidth, (int)(width * RightPanelRatio));
        rightPanelWidth = Math.Clamp(rightPanelWidth, 0, Math.Max(0, width - 2));

        var dividerColumn = rightPanelWidth > 0 ? 1 : 0;
        var mainWidth = width - rightPanelWidth - dividerColumn;

        var bodyHeight = Math.Max(0, height - StatusHeight - InputHeight - DividerRows);
        var inputY = Math.Min(bodyHeight + 1, Math.Max(0, height - InputHeight));
        var statusY = Math.Min(inputY + InputHeight + 1, Math.Max(0, height - StatusHeight));

        return new UiLayout(
            new ConsoleRect(0, 0, mainWidth, bodyHeight),
            new ConsoleRect(mainWidth + dividerColumn, 0, rightPanelWidth, bodyHeight),
            new ConsoleRect(0, inputY, width, InputHeight),
            new ConsoleRect(0, statusY, width, StatusHeight));
    }
}
