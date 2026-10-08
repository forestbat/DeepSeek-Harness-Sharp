namespace Dsh.Tui;

/** TUI 主题色(运行期可变): 由 TuiSettings 载入, 供渲染时引用, 支持真彩。 */
public static class TuiTheme
{
    public static CellColor Highlight { get; set; } = CellColor.FromPalette(AnsiColor.BrightCyan);

    public static void Load(Services.TuiSettings settings) => Highlight = settings.Highlight;
}
