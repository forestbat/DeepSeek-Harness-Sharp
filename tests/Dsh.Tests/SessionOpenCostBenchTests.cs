using System.Diagnostics;
using System.Text;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;

namespace Dsh.Tests;

/**
 * 定点探针: 打开真实会话时"宿主 + GUI 客户端 + 投影"的端到端代价与常驻内存。
 *
 * 它**不再承担打开性能基准的职责** —— 那是 SessionOpenCorpusBenchTests(真实语料逐份、冷热两态、逐行 CSV、按 §6.1 的列)。
 * 这里保留的三件事在持久化层之外, 语料基准量不到: 进程内宿主 + 无头 GUI 的端到端打开(含宿主握手)、
 * MainViewModel 的投影构造耗时与分配、打开前后进程常驻内存与托管堆增量。
 * 宿主与 GUI 客户端在同一个进程里(测试用的宿主是进程内监听), 因此内存数字是两者之和; 生产里它们是两个进程, 各占一部分。
 *
 * 候选是 5 份按量级挑的样本(百万条级到中等会话), 属定点取样而非逐份基准: 本机没有对应日志就跳过。
 * 会话日志来自本机真实目录, 只读复制进隔离 home(布局原样保留, 路径解析仍由生产代码决定)。
 */
[Collection("Bench")]
[Trait("Category", "Bench")]
public sealed class SessionOpenCostBenchTests
{
    /** 覆盖不同量级: 百万条级、二十万条级、十万条级与一份中等会话。 */
    private static readonly string[] Candidates =
    [
        "345b074a-52b8-456d-8f38-ce2db04d2eb4",
        "455709ad-3305-4270-85e6-483aa60b673e",
        "b596a42c-76e4-43de-9a1c-b145da1f5806",
        "32f77636-0564-4ad1-8371-722c834032fb",
        "cb7f2e70-8109-4a2c-b7bd-6951ad48fc63",
    ];

    private const long Megabyte = 1024 * 1024;

    [Fact(Explicit = true)]
    public async Task OpeningRealSessions_ReportsElapsedTimeAndResidentMemory()
    {
        var report = new StringBuilder();
        report.AppendLine("# 会话打开耗时与常驻内存实测");
        report.AppendLine();
        report.AppendLine("口径: 宿主与 GUI 客户端同进程(进程内监听), 内存是两者之和; 生产里宿主是独立进程。");
        report.AppendLine("冷开 = 本进程第一次打开这份日志(含一次性的帧正形与索引重建); 温开 = 紧接着再打开一次(索引与解析缓存已热)。");
        report.AppendLine();
        report.AppendLine("| 会话 | 日志 | 事件数 | 冷开 | 温开 | 投影(GUI) | 进程常驻(冷开前→冷开后) | 托管堆增加 |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

        var measured = 0;
        foreach (var candidate in Candidates)
        {
            var probe = TryLocate(candidate);
            if (probe is null)
                continue;
            var row = await MeasureAsync(probe, TestContext.Current.CancellationToken);
            report.AppendLine(row);
            measured += 1;
        }

        Assert.True(measured > 0, "本机没有可测的真实会话日志");
        report.AppendLine();
        report.AppendLine($"共测 {measured} 份会话。");
        BenchReport.Write("session-open-cost.md", report);
    }

    private static RealSessionProbe? TryLocate(string id)
    {
        try
        {
            return RealSessionProbe.Locate(id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string> MeasureAsync(RealSessionProbe probe, CancellationToken cancellationToken)
    {
        await using var host = await HarnessHostFixture.CreateAsync("bench", cancellationToken);
        probe.Seed(host.Environment.App.Home);
        // 宿主根先取出来: 下面那个工厂 lambda 活得比 host 的作用域更久, 直接捕 host 会被判"捕获的变量已释放"。
        var hostRoot = host.Root;
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        var options = FleetConnectionTests.TestOptions with { ClientName = "session-open-bench" };
        await using var fleet = new FleetWorkspace(new FleetWorkspaceServices(
            new MachineStore(gui.App.Home), options, () => new FleetClient([MachineSpec.Local(hostRoot)], options)));

        var log = Path.Combine(probe.ProjectDirectory, probe.Id.Value, "session.jsonl.zstd");
        var logMb = new FileInfo(log).Exists ? new FileInfo(log).Length / Megabyte : 0;
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var residentBefore = Process.GetCurrentProcess().WorkingSet64;

        var cold = Stopwatch.StartNew();
        HostChatSession? coldChat = null;
        var projectionMs = -1L;
        long projectionHeap = 0;
        var events = 0;
        await HeadlessGui.RunAsync(async () =>
        {
            coldChat = await HostChatSession.OpenAsync(fleet, probe.ProjectDirectory, probe.Id.Value, cancellationToken);
            cold.Stop();
            events = coldChat.Events.Count;
            var heapBeforeProjection = GC.GetTotalMemory(forceFullCollection: true);
            var projection = Stopwatch.StartNew();
            var viewModel = new MainViewModel(gui.App, coldChat);
            projection.Stop();
            projectionMs = projection.ElapsedMilliseconds;
            projectionHeap = GC.GetTotalMemory(forceFullCollection: true) - heapBeforeProjection;
            Assert.NotEmpty(viewModel.Messages);
        });
        var residentAfter = Process.GetCurrentProcess().WorkingSet64;
        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

        var warm = Stopwatch.StartNew();
        await HeadlessGui.RunAsync(async () =>
        {
            using var chat = await HostChatSession.OpenAsync(fleet, probe.ProjectDirectory, probe.Id.Value, cancellationToken);
            warm.Stop();
        });

        coldChat?.Dispose();
        return $"| {probe.Id.Value[..8]} | {logMb}MB | {events} | {cold.ElapsedMilliseconds}ms | {warm.ElapsedMilliseconds}ms "
            + $"| {projectionMs}ms | {(residentAfter - residentBefore) / Megabyte}MB | {(heapAfter - heapBefore) / Megabyte}MB "
            + $"(投影 {projectionHeap / Megabyte}MB) |";
    }
}