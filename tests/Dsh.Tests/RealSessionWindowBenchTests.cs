using System.Diagnostics;
using Dsh.Core;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 现场最大会话的窗口化打开代价(不含全量读那一半, 跑得快, 便于反复量)。
 * 全量对照见 RealSessionOpenBenchTests。
 */
[Trait("Category", "Bench")]
public sealed class RealSessionWindowBenchTests
{
    private const int WindowEvents = 2_000;

    [Fact]
    public void LargestRealSession_WindowedOpen()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
        if (!Directory.Exists(root))
            return;
        SessionPersistenceSnapshot? largest;
        using (var store = new JsonlSessionPersistence(root))
            largest = store.List().OrderByDescending(snapshot => snapshot.SizeBytes ?? 0).FirstOrDefault();
        if (largest is null || (largest.SizeBytes ?? 0) < 1024 * 1024)
            return;

        var sizeMb = (largest.SizeBytes ?? 0) / 1024d / 1024d;
        var logPath = JsonlLayout.LogPath(root, largest.Header.Cwd, largest.Header.Id, JsonlCompression.Zstd);
        var indexed = SessionLogIndex.TryLoad(logPath) is { } index ? index.Frames.Count : 0;
        var warnings = new List<string>();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var scanClock = Stopwatch.StartNew();
        long[]? boundaries;
        using (var scanStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            boundaries = ZstdFrames.ScanFrameBoundaries(scanStream);
        TestContext.Current.SendDiagnosticMessage(
            $"frame scan: {scanClock.ElapsedMilliseconds}ms frames={boundaries?.Length - 1 ?? -1}");
        using var store2 = new JsonlSessionPersistence(root, warn: warnings.Add);
        var clock = Stopwatch.StartNew();
        var window = store2.TryOpenWindow(largest.Header.Id, WindowEvents);
        var openMs = clock.ElapsedMilliseconds;
        if (window is not { } paged)
        {
            TestContext.Current.SendDiagnosticMessage(
                $"real session {sizeMb:N1}MB windowed open declined after {openMs}ms (indexed frames={indexed}): {string.Join("; ", warnings)}");
            return;
        }
        var snapshot = store2.Stat(largest.Header.Id)!;
        clock.Restart();
        var session = Session.FromWindow(largest.Header.Id, paged, snapshot.Header, snapshot.InheritedEventCount);
        var foldMs = clock.ElapsedMilliseconds;
        var heap = GC.GetTotalMemory(forceFullCollection: true);
        clock.Restart();
        var middle = session.EventAt(paged.ResidentFrom / 2);
        var pageMs = clock.ElapsedMilliseconds;
        TestContext.Current.SendDiagnosticMessage(
            $"real session {sizeMb:N1}MB windowed: total={paged.TotalEvents} resident={paged.Resident.Count} " +
            $"from={paged.ResidentFrom} open={openMs}ms fold={foldMs}ms middlePage={pageMs}ms ({middle?.Type ?? "missing"}) " +
            $"heap=+{(heap - baseline) / 1024d / 1024d:N1}MB faults={session.PageFaults} indexed={indexed}");
        Assert.Equal(paged.ResidentFrom, session.ResidentFrom);
    }
}
