using System.Text;

namespace Dsh.Tests;

/**
 * 临时调试驱动(用完删除): 起真 TUI, 等 artifacts/debug-screenshots/zz-go.txt 出现后才灌报文,
 * 这样调试器可以先 attach + 下好断点再触发, 不靠时间窗。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class ZzMouseDebugDriver
{
    [Fact]
    public async Task Drive()
    {
        using var harness = await PtyTuiHarness.StartAsync();
        if (harness is null)
            return;
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.WaitForAsync("快捷键", TimeSpan.FromSeconds(60));

        var trigger = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "debug-screenshots", "zz-go.txt"));
        var deadline = DateTime.UtcNow.AddMinutes(8);
        while (!File.Exists(trigger) && DateTime.UtcNow < deadline)
            await Task.Delay(500, cancellationToken);

        for (var step = 0; step < 4; step++)
        {
            await harness.WriteBytesAsync(Encoding.ASCII.GetBytes($"\u001b[M{(char)(32 + 32)}{(char)(32 + 20 + step)}{(char)(32 + 27)}"));
            await Task.Delay(60, cancellationToken);
        }

        await Task.Delay(1500, cancellationToken);
        for (var step = 0; step < 4; step++)
        {
            await harness.WriteBytesAsync(Encoding.ASCII.GetBytes($"\u001b[<32;{20 + step};27M"));
            await Task.Delay(60, cancellationToken);
        }

        await Task.Delay(90000, cancellationToken);
    }
}
