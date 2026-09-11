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
    public const int MaximumRightPanelWidth = 280;

    public static UiLayout Calculate(int consoleWidth, int consoleHeight)
    {
        var width = Math.Max(1, consoleWidth);
        var height = Math.Max(1, consoleHeight);

        var rightPanelWidth = Math.Min(MaximumRightPanelWidth, (int)(width * 0.30));
        rightPanelWidth = Math.Clamp(rightPanelWidth, 0, width - 1);

        var statusHeight = 1;
        var inputHeight = 1;
        var bodyHeight = Math.Max(0, height - statusHeight - inputHeight);
        var mainWidth = width - rightPanelWidth;
        var inputY = Math.Min(bodyHeight, Math.Max(0, height - inputHeight));
        var statusY = Math.Min(inputY + inputHeight, Math.Max(0, height - statusHeight));

        return new UiLayout(
            new ConsoleRect(0, 0, mainWidth, bodyHeight),
            new ConsoleRect(mainWidth, 0, rightPanelWidth, bodyHeight),
            new ConsoleRect(0, inputY, width, inputHeight),
            new ConsoleRect(0, statusY, width, statusHeight));
    }
}