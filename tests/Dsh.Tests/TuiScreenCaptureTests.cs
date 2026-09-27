using Dsh.Tui;
using OpenTK.Graphics.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dsh.Tests;

/**
 * 用**工程自身**的 PTY 与渲染管线出图: PtyHost 跑真实 TUI → 捕获输出 → VirtualTerminal 解析成字符屏
 * → GlyphAtlas/GpuRenderCore 离屏(EGL/GLFW 无头)光栅化成 PNG。不需要终端模拟器、不需要显示器。
 */
[Collection(SerialProcessCollection.Name)] // 真进程: 与其它真进程用例串行, 避免并行负载抖动
public class TuiScreenCaptureTests
{
    [Fact]
    [Trait("Category", "ScreenCapture")]
    public async Task Capture_Tui_Screen_After_Typing_And_ControlKey()
    {
        using var harness = await PtyTuiHarness.StartAsync();
        if (harness is null)
            return;
        await harness.WaitForAsync("快捷键", TimeSpan.FromSeconds(60));

        // ① 真实应用里 Ctrl+X + 分屏, 再拖动分隔线(等价真实指针事件的 SGR 序列)
        await harness.WriteBytesAsync(0x18);
        var prefixed = await harness.WaitForAsync("Ctrl+X:", TimeSpan.FromSeconds(10));
        Assert.Contains("Ctrl+X:", prefixed);
        await harness.WriteTextAsync("+");
        var split = await WaitForPaneDividerAsync(harness, TimeSpan.FromSeconds(15));
        var divider = FindPaneDividerColumn(split);
        Assert.True(divider > 0, "分屏后应出现窗格分隔线\n" + ScreenText(split));

        await harness.WriteTextAsync(Sgr(0, divider + 1, 2, press: true));
        await harness.WriteTextAsync(Sgr(32, divider + 7, 2, press: true));
        await harness.WriteTextAsync(Sgr(0, divider + 7, 2, press: false));
        await Task.Delay(800, TestContext.Current.CancellationToken);
        var dragged = harness.ReadScreen();
        Assert.Equal(divider + 6, FindPaneDividerColumn(dragged));
        SavePng(dragged, $"tui-screen-split-{PlatformTag()}.png");

        // ② 打字 + Ctrl+X 状态提示
        await harness.WriteTextAsync("zz");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await harness.WriteBytesAsync(0x18);
        await Task.Delay(800, TestContext.Current.CancellationToken);

        var screen = harness.ReadScreen();
        var rowsWithText = 0;
        for (var y = 0; y < screen.GetLength(1); y++)
        {
            var row = Row(screen, y);
            if (row.Trim().Length > 0)
                rowsWithText++;
        }

        SavePng(screen, $"tui-screen-{PlatformTag()}.png");
        Assert.True(rowsWithText > 3, $"捕获到的屏幕内容过少(仅 {rowsWithText} 行有文本)");
    }

    /** 轮询等分屏生效(新 agent 创建是异步的)。 */
    private static async Task<char[,]> WaitForPaneDividerAsync(PtyTuiHarness harness, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var screen = harness.ReadScreen();
        while (DateTime.UtcNow < deadline && FindPaneDividerColumn(screen) <= 0)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            screen = harness.ReadScreen();
        }
        return screen;
    }

    /** 窗格区域(x < 主区宽度)内的竖直分隔线; 右侧固定面板的分隔线在主区右边界, 天然排除。 */
    private static int FindPaneDividerColumn(char[,] screen)
    {
        var mainWidth = LayoutEngine.Calculate(PtyTuiHarness.Columns, PtyTuiHarness.Rows).Main.Width;
        for (var x = 1; x < mainWidth; x++)
        {
            for (var y = 0; y < PtyTuiHarness.Rows; y++)
            {
                if (screen[x, y] == '│')
                    return x;
            }
        }
        return -1;
    }

    /** SGR 鼠标序列(?1006 扩展坐标, x/y 从 1 开始): CSI < b ; x ; y M/m。 */
    private static string Sgr(int button, int x, int y, bool press)
        => $"\u001b[<{button};{x};{y}{(press ? 'M' : 'm')}";

    private static string ScreenText(char[,] screen)
    {
        var builder = new System.Text.StringBuilder();
        for (var y = 0; y < screen.GetLength(1); y++)
        {
            var row = Row(screen, y).TrimEnd();
            if (row.Length > 0)
                builder.Append(row).Append('\n');
        }
        return builder.ToString();
    }

    private static string PlatformTag()
        => OperatingSystem.IsWindows() ? "windows" : "linux";

    /** 字符屏 → 工程自身字形图集/GL 管线 → PNG(与 GpuRenderer.SaveScreenshot 同样的读回+翻转)。 */
    internal static void SavePng(char[,] screen, string fileName)
    {
        var width = screen.GetLength(0);
        var height = screen.GetLength(1);
        var grid = new CellGrid(width, height);
        for (var y = 0; y < PtyTuiHarness.Rows; y++)
            CellText.Draw(grid, 0, y, Row(screen, y));

        var atlas = GlyphAtlas.Shared;
        atlas.Prewarm();
        var pixelWidth = width * atlas.GlyphWidth;
        var pixelHeight = height * atlas.GlyphHeight;
        using var egl = HeadlessGl.Create(pixelWidth, pixelHeight);
        using var core = new GpuRenderCore();
        core.Initialize(atlas);
        GL.Viewport(0, 0, pixelWidth, pixelHeight);
        var packed = new uint[width * height];
        CellPacker.PackRows(grid, 0, height, packed, atlas);
        core.EnsureCellCapacity(packed.Length);
        core.UploadCells(packed, 0, packed.Length);
        core.RenderFrame(atlas, width, height);
        GL.Finish();

        var pixels = new byte[pixelWidth * pixelHeight * 4];
        GL.ReadPixels(0, 0, pixelWidth, pixelHeight, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        using var image = new Image<Rgba32>(pixelWidth, pixelHeight);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var sourceY = accessor.Height - 1 - y;
                for (var x = 0; x < accessor.Width; x++)
                {
                    var source = ((sourceY * accessor.Width) + x) * 4;
                    row[x] = new Rgba32(pixels[source], pixels[source + 1], pixels[source + 2], pixels[source + 3]);
                }
            }
        });

        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "debug-screenshots", fileName));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        image.SaveAsPng(path);
    }

    private static string Row(char[,] screen, int y)
    {
        var width = screen.GetLength(0);
        var chars = new char[width];
        for (var x = 0; x < width; x++)
        {
            var character = screen[x, y];
            chars[x] = character == '\0' ? ' ' : character;
        }
        return new string(chars);
    }
}




