namespace Dsh.Tests;

/**
 * 命令浮层/参数面板取证: 真实 TUI 跑在工程自身 PTY 上, 逐条命令进入参数阶段后,
 * 用工程自身渲染管线(字符屏 → GlyphAtlas/GpuRenderCore)光栅化成 PNG 落 artifacts/debug-screenshots/。
 * 不用 tmux、不用显示器、不用 mock LLM。
 */
[Collection(SerialProcessCollection.Name)]
public class TuiMenuCaptureTests
{
    [Fact]
    [Trait("Category", "ScreenCapture")]
    public async Task Capture_Menu_And_Argument_Panels()
    {
        using var harness = await PtyTuiHarness.StartAsync();
        if (harness is null)
            return;
        await harness.WaitForAsync("快捷键", TimeSpan.FromSeconds(60));

        // 每条状态: 进入参数阶段(面板 + 候选) → 截图 → 断言面板确实出现 → 关浮层并清空输入行。
        await CaptureAsync(harness, "model", "Provider/model", "/model", "\r");
        await CaptureAsync(harness, "provider-add", "Provider name", "/provider", "\r", "add", "\r");
        await CaptureAsync(harness, "provider-remove", "Provider name", "/provider", "\r", "remove", "\r");
        await CaptureAsync(harness, "skill", "Skill name", "/skill", "\r");
        await CaptureAsync(harness, "reasoning", "Reasoning effort", "/reasoning", "\r");
        await CaptureAsync(harness, "memory", "on, off, or show", "/memory", "\r");
        await CaptureAsync(harness, "session", "Session id or title", "/session", "\r");
        harness.SaveTranscript();
    }

    private static async Task CaptureAsync(PtyTuiHarness harness, string name, string marker, params string[] steps)
    {
        foreach (var step in steps)
        {
            await harness.WriteTextAsync(step);
            await Task.Delay(400, TestContext.Current.CancellationToken);
        }

        var screen = harness.ReadScreen();
        var text = TuiScreenCaptureTests.ScreenText(screen);
        TuiScreenCaptureTests.SavePng(screen, $"tui-menu-{name}-{TuiScreenCaptureTests.PlatformTag()}.png");
        // 文本与图片成对留证: 断言用 ASCII 标记(测试侧字符屏对 CJK 的宽度处理不影响取证)。
        File.WriteAllText(TuiScreenCaptureTests.ArtifactPath($"tui-menu-{name}-{TuiScreenCaptureTests.PlatformTag()}.txt"), text);
        Assert.Contains(marker, text);

        // Esc 逐级关浮层, 再用退格把输入行清空(输入行为空时退格是空操作)。
        await harness.WriteBytesAsync(0x1b, 0x1b);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var backspaces = new byte[48];
        Array.Fill(backspaces, (byte)0x7f);
        await harness.WriteBytesAsync(backspaces);
        await Task.Delay(300, TestContext.Current.CancellationToken);
    }
}
