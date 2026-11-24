using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 对抗复核用的临时测量工装(不是回归用例)。
 *
 * 只在 DSH_ADV_AUDIT 存在时执行, 平时立即返回, 因此不影响常规测试运行。
 * 量的四件事:
 * 1. 冷路径与投影缓存命中路径分别耗时多少, 同一进程内连续打开抖多大;
 * 2. 缓存命中且没有新事件时, ProjectionCache.SaveAsync 会不会把缓存文件重写一遍;
 * 3. Session.FromWindow 在有无 *.surface 派生缓存两种情况下 DeriveMessages 的条数与耗时;
 * 4. 冷开一份会话的托管分配量(GC.GetTotalAllocatedBytes)。
 *
 * 会话根由 DSH_ADV_SESSION_ROOT 指定(默认 ~/.dsh/sessions), 会话号由 DSH_ADV_SESSION_ID 指定。
 * 报告写到 DSH_ADV_OUT 指定的文件, 同时打到标准输出。
 */
[Collection("Bench")]
[Trait("Category", "AdvAudit")]
public sealed class AdvAuditOpenBenchTests
{
    private const long Megabyte = 1024 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(20);

    private static bool Enabled => Environment.GetEnvironmentVariable("DSH_ADV_AUDIT") is { Length: > 0 };

    private static (string Project, string Id, string Directory) Locate()
    {
        var root = Environment.GetEnvironmentVariable("DSH_ADV_SESSION_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
        var wanted = Environment.GetEnvironmentVariable("DSH_ADV_SESSION_ID")
            ?? throw new InvalidOperationException("DSH_ADV_SESSION_ID 未设置");
        foreach (var project in Directory.EnumerateDirectories(root))
        {
            var directory = Path.Combine(project, wanted);
            if (File.Exists(Path.Combine(directory, "session.jsonl.zstd")))
                return (Path.GetFileName(project), wanted, directory);
        }
        throw new InvalidOperationException($"没有找到会话 {wanted} (根 {root})");
    }

    /** 冷路径 vs 缓存命中: 同一进程内连续打开 N 次, 每次打开后都构造一次 MainViewModel(真实打开流程)。 */
    [Fact(Timeout = 3600000)]
    public async Task Audit_ColdVersusCachedOpen()
    {
        if (!Enabled)
            return;
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("DSH_ADV_ROUNDS"), out var parsed) ? parsed : 4;
        var (project, id, directory) = Locate();
        var logBytes = new FileInfo(Path.Combine(directory, "session.jsonl.zstd")).Length;
        using var budget = new CancellationTokenSource(Budget);
        await using var host = await HarnessHostFixture.CreateAsync("adv", budget.Token);
        Seed(directory, host.Environment.App.Home);
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        var cache = ProjectionCache.ForHome(gui.App.Home.Root);
        var cachePath = cache.PathFor(MachineSpec.LocalName, id);
        var hostRoot = host.Root;
        var options = FleetConnectionTests.TestOptions with { ClientName = "adv-audit" };
        await using var fleet = new FleetWorkspace(new FleetWorkspaceServices(
            new MachineStore(gui.App.Home), options, () => new FleetClient([MachineSpec.Local(hostRoot)], options)));

        var report = new StringBuilder();
        report.AppendLine("round,openMs,viewModelMs,cached,messages,events,totalEvents,allocatedMB,heapMB,cacheBytes,cacheRewritten,cacheDigest");
        var baseline = GC.GetTotalAllocatedBytes(precise: true);
        for (var round = 1; round <= rounds; round += 1)
        {
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
            HostChatSession? chat = null;
            MainViewModel? viewModel = null;
            var open = Stopwatch.StartNew();
            var viewModelMs = 0L;
            await HeadlessGui.RunAsync(async () =>
            {
                chat = await HostChatSession.OpenAsync(fleet, project, id, budget.Token, cache);
                open.Stop();
                var projection = Stopwatch.StartNew();
                viewModel = new MainViewModel(gui.App, chat);
                projection.Stop();
                viewModelMs = projection.ElapsedMilliseconds;
                Assert.NotEmpty(viewModel.Messages);
            }, Budget);
            var allocated = (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / Megabyte;
            var heap = (GC.GetTotalMemory(forceFullCollection: true) - heapBefore) / Megabyte;
            var cached = chat!.CachedProjection is not null;
            // 缓存写入是 fire-and-forget: 等它落盘; 命中轮还要等 mtime 变化才说明"重写了一遍"。
            var (bytes, rewritten, digest) = await WaitForCacheAsync(cachePath, expectRewrite: cached);
            report.AppendLine(string.Join(',', round, open.ElapsedMilliseconds, viewModelMs, cached, viewModel!.Messages.Count,
                chat.Events.Count, chat.TotalEventCount, allocated, heap, bytes, rewritten, digest));
            chat.Dispose();
        }
        report.AppendLine($"logBytes,{logBytes}");
        report.AppendLine($"allocatedTotalMB,{(GC.GetTotalAllocatedBytes(precise: true) - baseline) / Megabyte}");
        await EmitAsync(report, TestContext.Current.CancellationToken);
    }

    /**
     * 等缓存落盘。`expectRewrite=true` 时说明这次应当是缓存命中, 于是等"文件被重写"(mtime 变了),
     * 用来证明"命中也要重写一遍"。返回 (文件字节数, 是否观察到重写, 内容的 SHA256 前 16 位)。
     */
    private static async Task<(long Bytes, string Rewritten, string Digest)> WaitForCacheAsync(string path, bool expectRewrite)
    {
        var before = File.Exists(path) ? new FileInfo(path).LastWriteTimeUtc : DateTime.MinValue;
        for (var attempt = 0; attempt < 1200; attempt += 1)
        {
            await Task.Delay(100);
            if (!File.Exists(path))
                continue;
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc != before)
                return (info.Length, expectRewrite ? "rewritten" : "written", await DigestAsync(path));
        }
        return File.Exists(path)
            ? (new FileInfo(path).Length, "unchanged", await DigestAsync(path))
            : (-1, "missing", "-");
    }

    private static async Task<string> DigestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken);
        return Convert.ToHexString(hash)[..16];
    }

    private static void Seed(string sessionDirectory, HarnessHome home)
    {
        var project = Path.GetFileName(Path.GetDirectoryName(sessionDirectory)!);
        var target = Path.Combine(home.SessionsPath, project, Path.GetFileName(sessionDirectory));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(sessionDirectory))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
    }

    /**
     * 缓存写回的三段成本: 抓投影(CaptureProjection, 同步, 在调用线程上)、编码(Encode)、落盘。
     * 用来核对"编码是否在首个 await 之前同步执行"以及它到底值多少毫秒。
     */
    [Fact(Timeout = 3600000)]
    public async Task Audit_ProjectionEncodeCost()
    {
        if (!Enabled)
            return;
        var (project, id, directory) = Locate();
        using var budget = new CancellationTokenSource(Budget);
        await using var host = await HarnessHostFixture.CreateAsync("adv", budget.Token);
        Seed(directory, host.Environment.App.Home);
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        var cache = ProjectionCache.ForHome(gui.App.Home.Root);
        var hostRoot = host.Root;
        var options = FleetConnectionTests.TestOptions with { ClientName = "adv-audit" };
        await using var fleet = new FleetWorkspace(new FleetWorkspaceServices(
            new MachineStore(gui.App.Home), options, () => new FleetClient([MachineSpec.Local(hostRoot)], options)));
        var report = new StringBuilder();
        report.AppendLine("phase,ms,allocatedMB,messages");
        await HeadlessGui.RunAsync(async () =>
        {
            using var chat = await HostChatSession.OpenAsync(fleet, project, id, budget.Token, cache);
            var viewModel = new MainViewModel(gui.App, chat);
            Assert.NotEmpty(viewModel.Messages);
            for (var round = 1; round <= 2; round += 1)
            {
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var clock = Stopwatch.StartNew();
                var snapshot = viewModel.CaptureProjection(chat.AnchorFor(chat.TotalEventCount));
                var capturedAt = clock.ElapsedMilliseconds;
                var bytes = ProjectionSnapshotCodec.Encode(snapshot);
                var encodedAt = clock.ElapsedMilliseconds;
                await cache.SaveAsync(MachineSpec.LocalName, id, snapshot, budget.Token);
                var savedAt = clock.ElapsedMilliseconds;
                var allocated = (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / Megabyte;
                report.AppendLine(string.Join(',', $"round{round}-capture", capturedAt, allocated, snapshot.Messages.Count));
                report.AppendLine(string.Join(',', $"round{round}-encode", encodedAt - capturedAt, bytes.Length, snapshot.Trace.Count));
                report.AppendLine(string.Join(',', $"round{round}-saveAll", savedAt, savedAt - encodedAt, snapshot.Diffs.Count));
                var loadClock = Stopwatch.StartNew();
                var loaded = cache.TryLoad(MachineSpec.LocalName, id, snapshot.LogLength, snapshot.TotalEvents);
                loadClock.Stop();
                report.AppendLine(string.Join(',', $"round{round}-tryLoad", loadClock.ElapsedMilliseconds,
                    loaded is null ? "miss" : "hit", loaded?.Messages.Count ?? 0));
            }
        }, Budget);
        await EmitAsync(report, TestContext.Current.CancellationToken);
    }

    /**
     * 窗口化恢复的几种走法各量一遍: 整份恢复、窗口+派生缓存、窗口无派生缓存。
     * 同时报 DeriveMessages 的条数 —— 三者一致才谈得上"窗口化没有少给上下文"。
     */
    [Fact(Timeout = 3600000)]
    public async Task Audit_WindowedVersusFull()
    {
        if (!Enabled)
            return;
        var (_, id, directory) = Locate();
        if (Environment.GetEnvironmentVariable("DSH_ADV_SURFACE") == "drop")
            foreach (var sidecar in Directory.EnumerateFiles(directory, "*.surface"))
                File.Delete(sidecar);
        var tails = (Environment.GetEnvironmentVariable("DSH_ADV_TAILS") ?? "20000,2000,200")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
            .ToList();
        var root = Path.GetDirectoryName(Path.GetDirectoryName(directory)!)!;
        var report = new StringBuilder();
        report.AppendLine("mode,tail,fold,events,messages,warnings,ms1,ms2,ms3,ms4,ms5");
        report.AppendLine(Full(root, id));
        foreach (var tail in tails)
            report.AppendLine(Window(root, id, tail, adoptSurface: true));
        foreach (var tail in tails)
            report.AppendLine(Window(root, id, tail, adoptSurface: false));
        await EmitAsync(report, TestContext.Current.CancellationToken);
    }

    private static string Full(string root, string id)
    {
        var warnings = new List<string>();
        using var persistence = new JsonlSessionPersistence(root, warn: warnings.Add);
        var sessionId = SessionId.Create(id);
        var clock = Stopwatch.StartNew();
        using var handle = persistence.Open(sessionId, SessionAccess.Read);
        var events = handle.Read();
        var readAt = clock.ElapsedMilliseconds;
        var session = Session.FromRestore(sessionId, events, handle.Header, handle.InheritedEventCount);
        var restoredAt = clock.ElapsedMilliseconds;
        var messages = session.DeriveMessages();
        var derivedAt = clock.ElapsedMilliseconds;
        return string.Join(',', "full", 0, "-", events.Count, messages.Count, warnings.Count,
            readAt, restoredAt - readAt, derivedAt - restoredAt, derivedAt, session.Seq);
    }

    private static string Window(string root, string id, int tailEvents, bool adoptSurface)
    {
        var warnings = new List<string>();
        using var persistence = new JsonlSessionPersistence(root, warn: warnings.Add);
        var sessionId = SessionId.Create(id);
        var clock = Stopwatch.StartNew();
        using var handle = persistence.Open(sessionId, SessionAccess.Read);
        var openedAt = clock.ElapsedMilliseconds;
        if (persistence.TryOpenWindow(sessionId, tailEvents) is not { } paged)
            return string.Join(',', "declined", tailEvents, "-", 0, 0, warnings.Count, openedAt, 0, 0, 0, 0);
        var windowAt = clock.ElapsedMilliseconds;
        var adopted = adoptSurface ? persistence.TryLoadSurfaceFold(sessionId) : null;
        var loadedAt = clock.ElapsedMilliseconds;
        var session = Session.FromWindow(sessionId, paged, handle.Header, handle.InheritedEventCount, adopted);
        var builtAt = clock.ElapsedMilliseconds;
        var messages = session.DeriveMessages();
        var derivedAt = clock.ElapsedMilliseconds;
        return string.Join(',', adoptSurface ? "window+fold" : "window-nofold", tailEvents,
            adopted is null ? "nofold" : "fold", paged.Resident.Count, messages.Count, warnings.Count,
            openedAt, windowAt - openedAt, loadedAt - windowAt, builtAt - loadedAt, derivedAt - builtAt);
    }

    private static async Task EmitAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        var outPath = Environment.GetEnvironmentVariable("DSH_ADV_OUT")
            ?? Path.Combine(AppContext.BaseDirectory, "adv-audit.txt");
        await File.WriteAllTextAsync(outPath, report.ToString(), cancellationToken);
        await Console.Out.WriteAsync(report.ToString(), cancellationToken);
    }
}
