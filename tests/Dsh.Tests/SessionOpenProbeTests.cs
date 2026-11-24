using Dsh.Gui.Services;

namespace Dsh.Tests;

/**
 * 打开会话的 GUI 侧打点(方案 §6.3): 首屏/可输入/主线程最长阻塞。
 *
 * 这里只验"探针能在 UI 线程上跑、并给出可解析的一行"。真实的毫秒数由真机打开会话时量, 不在这里断言数值。
 */
public sealed class SessionOpenProbeTests
{
    [Fact]
    public async Task Probe_ReportsAllThreeScales() => await HeadlessGui.RunAsync(async () =>
    {
        var probe = new SessionOpenProbe();
        await Task.Delay(80);
        probe.MarkFirstPaint();
        probe.MarkInteractive();
        await Task.Delay(40);
        var line = probe.Finish("session-probe", probe.ElapsedMs, events: 12);

        Assert.StartsWith("session paint: id=session-probe ", line, StringComparison.Ordinal);
        var fields = line.Split(' ').Skip(2)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        Assert.True(long.Parse(fields["firstPaint_ms"]) >= 0, line);
        Assert.True(long.Parse(fields["interactive_ms"]) >= 0, line);
        Assert.True(long.Parse(fields["stall_ms"]) >= 0, line);
        Assert.True(long.Parse(fields["total_ms"]) >= long.Parse(fields["interactive_ms"]), line);
        Assert.Equal("12", fields["events"]);
    });
}
