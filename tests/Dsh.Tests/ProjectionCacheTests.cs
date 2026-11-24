using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Interaction;
using Dsh.Llm;
using System.Text;
using System.Text.Json;

namespace Dsh.Tests;

/**
 * 投影缓存(阶段 3)的验收: **缓存恢复出来的界面必须与新鲜投影逐项相同**。
 *
 * 这类缓存一旦有偏差, 表现是"重开之后少一段/样式不同", 比慢难排查得多, 所以这里比较的是视图模型的
 * 全部可见字段(消息、轨迹、统计、交付物), 而不是"看起来差不多"。
 * 两处有意的排除: 轨迹时刻(取投影那一刻的墙上时间, 两次投影必然不同)与耗时类统计(重放时机器的快慢)。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class ProjectionCacheTests
{
    /**
     * 忙着的界面不入缓存: 那个回合已经随进程结束, 存下来就是"一打开就按不动停止按钮"的死界面。
     * 到达日志末尾但仍在跑的这一份同样不能存。
     */
    [Fact]
    public async Task Store_RejectsABusyProjection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-projection-busy-{Guid.NewGuid():N}");
        try
        {
            var cache = new ProjectionCache(root);
            var busy = Snapshot(nextSeq: 8, totalEvents: 8) with { IsBusy = true };
            var compaction = Snapshot(nextSeq: 8, totalEvents: 8) with { CompactionBusy = true };

            await cache.SaveAsync("local", "busy-1", busy, TestContext.Current.CancellationToken);
            await cache.SaveAsync("local", "busy-2", compaction, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(cache.PathFor("local", "busy-1")), "a busy projection must not be cached");
            Assert.False(File.Exists(cache.PathFor("local", "busy-2")), "a compaction-busy projection must not be cached");
            // 同一份内容把忙碌位清掉就照常存: 判据只有"忙不忙", 不是别的副作用。
            await cache.SaveAsync("local", "idle", busy with { IsBusy = false }, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(cache.PathFor("local", "idle")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /** 一份最小的合法投影: 一条用户消息, 覆盖到第 nextSeq 条, 日志修订 4096 字节。 */
    private static ProjectionSnapshot Snapshot(long nextSeq, long totalEvents) => new()
    {
        FormatVersion = ProjectionSnapshot.CurrentFormatVersion,
        NextSeq = nextSeq,
        TotalEvents = totalEvents,
        LogLength = 4096,
        LastEventTime = 1700000000000,
        LastEventType = "user/message",
        RenderedAny = true,
        IsBusy = false,
        CompactionBusy = false,
        Messages = [new MessageSnapshot
        {
            Role = "你", Kind = (int)MessageKind.User, Text = "hi", RenderSeq = 0, Detail = "",
            IsExpanded = true, IsStreaming = false, Feedback = 0, ToolName = "", ArgumentText = "",
            ResultText = "", HasResult = false, IsToolError = false, IsDiffExpanded = false, IsSubagentTool = false,
        }],
        Trace = [],
        LastUserMessageSeqs = [0],
        PendingToolCalls = [],
        Tokens = new TokenStatsSnapshot(0, 0, 0, null, 0, ""),
        Deliverables = [],
        Diffs = [],
    };

    /** 存储层自己的规矩: 修订至少不能变小、锚点必须指同一条事件、坏文件当作没命中。 */
    [Fact]
    public async Task Store_RejectsStaleRevisionsAndCorruptFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-projection-cache-{Guid.NewGuid():N}");
        var cache = new ProjectionCache(root);
        var snapshot = Snapshot(nextSeq: 8, totalEvents: 8);
        await cache.SaveAsync("local", "s-1", snapshot, TestContext.Current.CancellationToken);

        var path = cache.PathFor("local", "s-1");
        Assert.True(File.Exists(path), $"cache file missing: {path}");
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.NotNull(ProjectionSnapshotCodec.Decode(bytes));
        var loaded = cache.TryLoad("local", "s-1", 4096, 8);
        Assert.NotNull(loaded);
        Assert.Equal(8, loaded.NextSeq);
        Assert.Equal("hi", Assert.Single(loaded.Messages).Text);
        // 日志变短(回退)或条数变少: 前缀不再可信。
        Assert.Null(cache.TryLoad("local", "s-1", 2048, 8));
        Assert.Null(cache.TryLoad("local", "s-1", 4096, 4));
        // 锚点: 同位置的那条事件换了(回退后又追加到同样长度)就不认。
        Assert.True(ProjectionCache.AnchorMatches(loaded, Anchor(7, 1700000000000, "user/message")));
        Assert.False(ProjectionCache.AnchorMatches(loaded, Anchor(7, 1700000000001, "user/message")));
        Assert.False(ProjectionCache.AnchorMatches(loaded, Anchor(7, 1700000000000, "assistant/message")));
        Assert.False(ProjectionCache.AnchorMatches(loaded, Anchor(6, 1700000000000, "user/message")));

        await File.WriteAllBytesAsync(
            cache.PathFor("local", "s-1"), [9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9], TestContext.Current.CancellationToken);
        Assert.Null(cache.TryLoad("local", "s-1", 4096, 8));
        Directory.Delete(root, recursive: true);
    }

    private static SessionEvent Anchor(long seq, long time, string type)
        => new SessionEvent { Type = type, Seq = seq, Time = time, Data = new TurnStartPayload(1) };
    [Fact]
    public async Task CachedProjection_EqualsAFreshReplay() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        AppendTranscript(environment.Agent.Session);
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var id = environment.Agent.Id;

        var first = await environment.OpenChatAsync(id, cache);
        Assert.Null(first.CachedProjection);
        var fresh = new MainViewModel(environment.App, first);
        var expected = Dump(fresh);
        Assert.NotEmpty(fresh.Messages);
        await fresh.SaveProjectionAsync();

        var second = await environment.OpenChatAsync(id, cache);
        Assert.NotNull(second.CachedProjection);
        // 命中缓存意味着整段历史没有取回来: 增量正好是空的。
        Assert.Empty(second.Events);
        await using var restored = new MainViewModel(environment.App, second);
        Assert.Equal(expected, Dump(restored));
    });

    /** 缓存之后又追加了事件: 只剩下增量要走重放, 结果仍须与整段重放一致。 */
    [Fact]
    public async Task CachePlusDelta_EqualsAFreshReplay() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var session = environment.Agent.Session;
        AppendTranscript(session);
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var id = environment.Agent.Id;

        var first = await environment.OpenChatAsync(id, cache);
        var fresh = new MainViewModel(environment.App, first);
        await fresh.SaveProjectionAsync();
        first.Dispose();

        Append(session, new TurnStartPayload(2));
        Append(session, new UserMessagePayload(new UserMessage
        {
            Id = MessageFactory.NewId(),
            Content = [new TextBlock("再补一条消息")],
        }));
        Append(session, new TurnEndPayload(2, new TurnEndReason.Completed()));
        await FlushSessionsAsync(environment);

        var second = await environment.OpenChatAsync(id, cache);
        Assert.NotNull(second.CachedProjection);
        Assert.NotEmpty(second.Events);
        Assert.All(second.Events, sessionEvent => Assert.True(sessionEvent.Seq >= second.CachedProjection.NextSeq));
        var delta = new MainViewModel(environment.App, second);

        var whole = await environment.OpenChatAsync(id);
        Assert.Null(whole.CachedProjection);
        await using var reference = new MainViewModel(environment.App, whole);
        Assert.Equal(Dump(reference), Dump(delta));
    });

    /** 缓存命中的会话上重载只追加了尾部时不许重建整屏(切模型/推理档位就是这条路径)。 */
    [Fact]
    public async Task ReloadOnACachedSession_DoesNotResetTheTranscript() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var session = environment.Agent.Session;
        AppendTranscript(session);
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var id = environment.Agent.Id;

        var first = await environment.OpenChatAsync(id, cache);
        await new MainViewModel(environment.App, first).SaveProjectionAsync();
        first.Dispose();

        var second = await environment.OpenChatAsync(id, cache);
        Assert.NotNull(second.CachedProjection);
        var resets = 0;
        second.Reset += () => resets += 1;
        try
        {
            Append(session, new TurnStartPayload(2));
            Append(session, new UserMessagePayload(new UserMessage
            {
                Id = MessageFactory.NewId(),
                Content = [new TextBlock("切档位之后的一条")],
            }));
            Append(session, new TurnEndPayload(2, new TurnEndReason.Completed()));
            await FlushSessionsAsync(environment);

            await second.ReloadAsync();

            Assert.Equal(0, resets);
            Assert.Contains(second.Events, sessionEvent => sessionEvent.Data is UserMessagePayload);
        }
        finally
        {
            second.Dispose();
        }
    });

    /**
     * 日志变短之后, 旧缓存不得再用: 修订对不上就整段重放。
     *
     * "变短"只有存储层这一个来源(打开时的损伤修复会丢掉读不出来的记录), 所以现场按字节层造:
     * 先 flush 出一份完整日志, 再用产品写入路径把前 5 条事件重新落盘覆盖掉它, 然后从磁盘重新打开。
     */
    [Fact]
    public async Task ShorterLog_DoesNotUseTheCache() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var session = environment.Agent.Session;
        AppendTranscript(session);
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var id = environment.Agent.Id;

        var first = await environment.OpenChatAsync(id, cache);
        var fresh = new MainViewModel(environment.App, first);
        await fresh.SaveProjectionAsync();
        first.Dispose();

        // 只留下"请求头/上下文/回合开始/步开始/用户消息"这 5 条: 助手回复与工具结果都在被丢掉的那一段里。
        RewriteLogWithTheFirstEvents(environment, session, keep: 5);

        var second = await environment.OpenChatAsync(id, cache);
        Assert.Null(second.CachedProjection);
        await using var replayed = new MainViewModel(environment.App, second);
        Assert.DoesNotContain(replayed.Messages, message => message.Text.Contains("看完了", StringComparison.Ordinal));
    });

    /**
     * 把会话日志换成"只含前 keep 条事件"的那一份: 事件从活会话取, 编码与落盘全走产品的写入路径。
     * 活会话要从登记表里放掉, 否则宿主拿内存那份喂界面, 磁盘上变短了也看不出来。
     */
    private static void RewriteLogWithTheFirstEvents(GuiTestEnvironment environment, Session session, int keep)
    {
        var events = session.SnapshotEvents().Take(keep).ToArray();
        var persistence = environment.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
        environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Release(session.Id);
        persistence.Delete(session.Id);
        using var handle = persistence.Create(session.Header);
        handle.Append(events);
        handle.Flush();
    }

    /** 缓存文件坏了(半截/乱码)不许影响会话: 当作没命中, 照常整段重放。 */
    [Fact]
    public async Task CorruptCacheFile_FallsBackToFullReplay() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        AppendTranscript(environment.Agent.Session);
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var id = environment.Agent.Id;

        var first = await environment.OpenChatAsync(id, cache);
        var fresh = new MainViewModel(environment.App, first);
        var expected = Dump(fresh);
        await fresh.SaveProjectionAsync();
        first.Dispose();

        var path = cache.PathFor(MachineSpec.LocalName, id.Value);
        Assert.True(File.Exists(path));
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], TestContext.Current.CancellationToken);

        var second = await environment.OpenChatAsync(id, cache);
        Assert.Null(second.CachedProjection);
        await using var replayed = new MainViewModel(environment.App, second);
        Assert.Equal(expected, Dump(replayed));
    });

    /** 用户自己的界面状态(折叠、点赞、diff 卡片的展开)也要跟着投影一起活下来。 */
    [Fact]
    public async Task UserInterfaceStateSurvivesTheCache() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        AppendTranscript(environment.Agent.Session);
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var id = environment.Agent.Id;

        var first = await environment.OpenChatAsync(id, cache);
        var viewModel = new MainViewModel(environment.App, first);
        {
            var assistant = viewModel.Messages.First(message => message.Kind == MessageKind.Assistant && message.Text.Length > 0);
            assistant.Feedback = MessageFeedback.Disliked;
            var tool = viewModel.Messages.First(message => message.Kind == MessageKind.Tool);
            tool.IsExpanded = true;
            tool.SetDiff(new DiffCard("a.txt", "meta", [new DiffLine(DiffLineKind.Add, null, 1, "+hi")]));
            tool.IsDiffExpanded = true;
            await viewModel.SaveProjectionAsync();
        }
        first.Dispose();

        var second = await environment.OpenChatAsync(id, cache);
        Assert.NotNull(second.CachedProjection);
        await using var restored = new MainViewModel(environment.App, second);
        var restoredAssistant = restored.Messages.First(message => message.Kind == MessageKind.Assistant && message.Text.Length > 0);
        Assert.Equal(MessageFeedback.Disliked, restoredAssistant.Feedback);
        var restoredTool = restored.Messages.First(message => message.Kind == MessageKind.Tool);
        Assert.True(restoredTool.IsExpanded);
        Assert.True(restoredTool.IsDiffExpanded);
        Assert.Equal("a.txt", Assert.IsType<DiffCard>(restoredTool.Diff).Title);
        Assert.True(restoredTool.HasDiff);
    });

    /**
     * 命中缓存且没有新事件时: 连续打开同一会话不许重写缓存文件。
     *
     * 重写的代价在抓快照那一步(UI 线程遍历整个投影, 实测单次 940–1356MB 分配)+ 编码(~1s)+ 写 5.6MB,
     * 而存下去的与磁盘上那份覆盖的是同一条末事件。判据成立时字节与 mtime 都不该动。
     */
    [Fact]
    public async Task ReopeningAnUnchangedSession_DoesNotRewriteTheCache() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        AppendTranscript(environment.Agent.Session);
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var id = environment.Agent.Id;
        var path = cache.PathFor(MachineSpec.LocalName, id.Value);

        var first = await environment.OpenChatAsync(id, cache);
        // 不 Dispose 这个视图模型: 它会连 Fleet 一起关掉, 而下面还要在同一个环境里重开会话。
        var fresh = new MainViewModel(environment.App, first);
        await fresh.SaveProjectionAsync();
        first.Dispose();
        Assert.True(File.Exists(path), $"cache file missing: {path}");
        var writtenBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var writtenAt = await SettledWriteTimeAsync(path);

        for (var round = 1; round <= 3; round += 1)
        {
            var again = await environment.OpenChatAsync(id, cache);
            Assert.NotNull(again.CachedProjection);
            var reopened = new MainViewModel(environment.App, again);
            await reopened.SaveProjectionAsync();
            again.Dispose();
            // 视图模型构造时那次保存是"发了不管"的, 等文件写静下来再断言, 免得把迟到的写入算成通过。
            Assert.Equal(writtenAt, await SettledWriteTimeAsync(path));
            Assert.Equal(writtenBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
    });

    /** 等缓存文件的 mtime 写静下来: 打开会话时那次保存是后台发起的, 可能晚于显式等待的那次落盘。 */
    private static async Task<DateTime> SettledWriteTimeAsync(string path)
    {
        var seen = File.GetLastWriteTimeUtc(path);
        for (var attempt = 0; attempt < 40; attempt += 1)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            var current = File.GetLastWriteTimeUtc(path);
            if (current == seen)
                return current;
            seen = current;
        }
        return seen;
    }

    /** 上一用例的反面: 追加了新事件之后仍要重写 —— 判据是"修订没变", 不是"有缓存就不写"。 */
    [Fact]
    public async Task AppendingEvents_RewritesTheCache() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var session = environment.Agent.Session;
        AppendTranscript(session);
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var id = environment.Agent.Id;
        var path = cache.PathFor(MachineSpec.LocalName, id.Value);

        var first = await environment.OpenChatAsync(id, cache);
        var fresh = new MainViewModel(environment.App, first);
        await fresh.SaveProjectionAsync();
        first.Dispose();
        var written = ProjectionSnapshotCodec.Decode(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.NotNull(written);

        Append(session, new TurnStartPayload(2));
        Append(session, new UserMessagePayload(new UserMessage
        {
            Id = MessageFactory.NewId(),
            Content = [new TextBlock("再补一条")],
        }));
        Append(session, new TurnEndPayload(2, new TurnEndReason.Completed()));
        await FlushSessionsAsync(environment);

        var second = await environment.OpenChatAsync(id, cache);
        Assert.NotNull(second.CachedProjection);
        var reopened = new MainViewModel(environment.App, second);
        await reopened.SaveProjectionAsync();
        second.Dispose();

        var rewritten = ProjectionSnapshotCodec.Decode(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.NotNull(rewritten);
        Assert.True(rewritten.NextSeq > written.NextSeq, $"cache was not rewritten: {written.NextSeq} -> {rewritten.NextSeq}");
    });

    /**
     * 大 diff 的正文不进缓存: 只留前 MaxStoredLines 行预览 + 来源事件序号; 展开时回头取原始事件,
     * 按重放同一条提取路径(DiffCardExtractor)还原全文。
     */
    [Fact]
    public async Task DiffBodyIsStoredAsAPreview_AndRefetchedWhenExpanded() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        AppendDiffTranscript(environment.Agent.Session, DiffTranscriptLines);
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var id = environment.Agent.Id;
        var path = cache.PathFor(MachineSpec.LocalName, id.Value);

        var first = await environment.OpenChatAsync(id, cache);
        var fresh = new MainViewModel(environment.App, first);
        Assert.Equal(DiffTranscriptLines, Assert.Single(fresh.Messages, message => message.HasDiff).Diff!.Lines.Count);
        await fresh.SaveProjectionAsync();
        first.Dispose();

        var stored = ProjectionSnapshotCodec.Decode(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.NotNull(stored);
        var storedCard = Assert.Single(stored.Diffs);
        Assert.Equal(DiffTranscriptLines, storedCard.LineCount);
        Assert.Equal(DiffCardSnapshot.MaxStoredLines, storedCard.Lines.Count);
        var storedMessage = stored.Messages.First(message => message.Diff is not null);
        Assert.NotNull(storedMessage.DiffResultSeq);

        var second = await environment.OpenChatAsync(id, cache);
        Assert.NotNull(second.CachedProjection);
        await using var restored = new MainViewModel(environment.App, second);
        var tool = restored.Messages.First(message => message.HasDiff);
        var preview = Assert.IsType<DiffCard>(tool.Diff);
        Assert.Equal(DiffTranscriptLines, preview.TotalLines);
        Assert.Equal(DiffCardSnapshot.MaxStoredLines, preview.Lines.Count);
        Assert.Equal(storedMessage.DiffResultSeq, tool.DiffSourceSeq);

        tool.IsDiffExpanded = true;
        await GuiTestEnvironment.WaitForAsync(tool, () => tool.Diff is { } card && card.Lines.Count == DiffTranscriptLines);
        Assert.Equal(DiffTranscriptLines, tool.Diff!.Lines.Count);
        Assert.Equal(DiffTranscriptLines, tool.Diff.TotalLines);
        // 正文到手之后不再需要来源序号: 再展开一次也不必回头取。
        Assert.Null(tool.DiffSourceSeq);
    });

    /**
     * 旧缓存(改动之前写下的)里存的是完整正文, 读出来必须照原样显示, 也不能被当成预览去回头取。
     * 旧文件与本格式只差两个成员(LineCount/DiffResultSeq), 魔数与版本一致, 所以这里按旧形态编一份写回去。
     */
    [Fact]
    public async Task CacheFromBeforeThePreviewField_StillShowsTheWholeDiffBody() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        AppendDiffTranscript(environment.Agent.Session, DiffTranscriptLines);
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var id = environment.Agent.Id;
        var path = cache.PathFor(MachineSpec.LocalName, id.Value);

        var first = await environment.OpenChatAsync(id, cache);
        var fresh = new MainViewModel(environment.App, first);
        await fresh.SaveProjectionAsync();
        // 重放路径上正文是全的(缓存截断只发生在快照里), 正好用来拼一份"改动之前"的缓存。
        var full = fresh.Messages.First(message => message.HasDiff).Diff!;
        first.Dispose();

        var current = ProjectionSnapshotCodec.Decode(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.NotNull(current);
        var old = current with
        {
            Diffs = [OldCard(full)],
            Messages = [.. current.Messages.Select(message => message with { DiffResultSeq = null })],
        };
        await File.WriteAllBytesAsync(path, ProjectionSnapshotCodec.Encode(old), TestContext.Current.CancellationToken);

        var second = await environment.OpenChatAsync(id, cache);
        Assert.NotNull(second.CachedProjection);
        await using var restored = new MainViewModel(environment.App, second);
        var tool = restored.Messages.First(message => message.HasDiff);
        var card = Assert.IsType<DiffCard>(tool.Diff);
        Assert.Equal(DiffTranscriptLines, card.Lines.Count);
        Assert.Equal(DiffTranscriptLines, card.TotalLines);
        Assert.Null(tool.DiffSourceSeq);
        // 没有来源序号 ⇒ 展开时不会有任何回取: 卡片对象原样留着。
        tool.IsDiffExpanded = true;
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Same(card, tool.Diff);
    });

    /**
     * 体积口径的实测(不是断言, 只是把数字报出来): 取本机真实缓存文件, 按"改动前(完整 diff 正文)"与
     * "改动后(只留前 MaxStoredLines 行)"各编码一次, 结果同时落到 artifacts/p1-cache-size/。
     * 真实缓存不存在时只写下"没有样本" —— 不另造一份假数据充数。
     */
    [Fact]
    public void RealCacheFiles_ShrinkWhenTheDiffBodyStopsBeingStored()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "cache", ProjectionCache.DirectoryName);
        var report = new List<string> { $"缓存目录: {root} (存在={Directory.Exists(root)})" };
        if (Directory.Exists(root))
        {
            // 缓存按机器分目录(root/<machine>/<session>.projection), 所以要递归找。
            foreach (var file in Directory.EnumerateFiles(root, "*.projection", SearchOption.AllDirectories)
                         .Select(path => new FileInfo(path))
                         .OrderByDescending(file => file.Length)
                         .Take(4))
                report.Add(MeasureOne(file));
        }
        foreach (var line in report)
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        var repo = RepositoryRoot();
        if (repo is null)
            return;
        var target = Path.Combine(repo, "artifacts", "p1-cache-size");
        Directory.CreateDirectory(target);
        File.WriteAllLines(Path.Combine(target, "real-cache-size.txt"), report);
    }

    /** 一份真实缓存的三种体积: 磁盘上的文件、改动前(完整 diff 正文)、改动后(只留预览)。 */
    private static string MeasureOne(FileInfo file)
    {
        var snapshot = ProjectionSnapshotCodec.Decode(File.ReadAllBytes(file.FullName));
        if (snapshot is null)
            return $"{file.Name}: 读不出来(版本不符或损坏), 跳过";
        var capped = snapshot with
        {
            Diffs = [.. snapshot.Diffs.Select(card => card with
            {
                Lines = [.. card.Lines.Take(DiffCardSnapshot.MaxStoredLines)],
                LineCount = card.Lines.Count,
            })],
        };
        var plain = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        var diffsPlain = JsonSerializer.SerializeToUtf8Bytes(snapshot.Diffs);
        var cappedPlain = JsonSerializer.SerializeToUtf8Bytes(capped);
        var keptLines = capped.Diffs.Sum(card => card.Lines.Count);
        var allLines = snapshot.Diffs.Sum(card => card.Lines.Count);
        return $"{file.Name}: 磁盘 {file.Length} B → 改动后 {ProjectionSnapshotCodec.Encode(capped).Length} B; "
               + $"明文 {Megabytes(plain.Length)} → {Megabytes(cappedPlain.Length)}; "
               + $"Diffs {snapshot.Diffs.Count} 张 {Megabytes(diffsPlain.Length)}({diffsPlain.Length * 100.0 / plain.Length:F1}%) "
               + $"{allLines} 行 → 留 {keptLines} 行";
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024.0 / 1024.0:F2}MB";

    /** 仓库根(报告落在 artifacts/ 下): 从测试程序集往上找带 src/Dsh.Gui 的那一级。 */
    private static string? RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Dsh.Gui")))
                return directory.FullName;
        }
        return null;
    }

    /** 一段带大 diff 的转录: 工具参数里是 DiffTranscriptLines 行的 unified diff, 提取出的卡片正好那么多行。 */
    private static void AppendDiffTranscript(Session session, int addedLines)
    {
        var callId = ToolCallId.Create("call-diff-1");
        var patch = new StringBuilder("--- a/big.txt\n+++ b/big.txt\n");
        patch.Append($"@@ -0,0 +1,{addedLines} @@\n");
        for (var line = 1; line <= addedLines; line += 1)
            patch.Append($"+line {line}\n");
        Append(session, new TurnStartPayload(1));
        Append(session, new StepStartPayload(1, 1));
        Append(session, new UserMessagePayload(new UserMessage
        {
            Id = MessageFactory.NewId(),
            Content = [new TextBlock("改一份大文件")],
        }));
        Append(session, new ToolCallPayload(1, 1, callId, "apply_patch", JsonSerializer.Serialize(new { patch = patch.ToString() })));
        Append(session, new ToolResultPayload(1, 1, new ToolResultMessage
        {
            Id = MessageFactory.NewId(),
            Content = [new ToolResultBlock(callId, [new TextBlock("已写入 big.txt")])],
            ToolSource = new ToolMessageSource(callId),
        }));
        Append(session, new AssistantMessagePayload(1, 1, new AssistantMessage
        {
            Id = MessageFactory.NewId(),
            Content = [new TextBlock("改完了。")],
            ModelSource = new ModelMessageSource("deepseek-official", "deepseek-v4-flash"),
        }, new TokenUsage(10, 20, CacheReadTokens: 0)));
        Append(session, new TurnEndPayload(1, new TurnEndReason.Completed()));
    }

    /** 旧格式的卡片: 正文全在, 没有 LineCount(读出来是 0 ⇒ 视为"Lines 就是全部")。 */
    private static DiffCardSnapshot OldCard(DiffCard card)
        => new(card.Title, card.Source, [.. card.Lines.Select(line => new DiffLineSnapshot((int)line.Kind, line.OldLine, line.NewLine, line.Text))]);

    private const int DiffTranscriptLines = 600;

    /** 写盘一次: 宿主报的修订(日志长度/事件条数)取自磁盘, 不落盘就等于"日志没变"。 */
    private static async Task FlushSessionsAsync(GuiTestEnvironment environment)
    {
        var store = environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        foreach (var live in store.List())
            await store.Flush(live);
    }

    /** 一段覆盖各类渲染分支的转录: 思考/正文/工具/结果/交付物/审批/统计。 */
    private static void AppendTranscript(Session session)
    {
        var config = new LlmCallConfig("deepseek-official", "deepseek-v4-flash", ReasoningEffortId.Create("high"));
        var callId = ToolCallId.Create("call-cache-1");
        Append(session, new RequestHeaderPayload(new EpochHeader(config), RequestHeaderReasons.Initial));
        Append(session, new RequestContextPayload("deepseek-official", "deepseek-v4-flash", 1_000_000));
        Append(session, new TurnStartPayload(1));
        Append(session, new StepStartPayload(1, 1));
        Append(session, new UserMessagePayload(new UserMessage
        {
            Id = MessageFactory.NewId(),
            Content = [new TextBlock("把方案落成代码")],
        }));
        Append(session, new AssistantChunkPayload(1, 1, new StreamChunk.BlockStart(0, "reasoning")));
        Append(session, new AssistantChunkPayload(1, 1, new StreamChunk.ReasoningDelta(0, "先确认现有投影")));
        Append(session, new AssistantChunkPayload(1, 1, new StreamChunk.BlockStart(1, "text")));
        Append(session, new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(1, "我先看一遍代码。")));
        Append(session, new ToolCallPayload(1, 1, callId, "bash", """{"command":"ls"}"""));
        Append(session, new ToolResultPayload(1, 1, new ToolResultMessage
        {
            Id = MessageFactory.NewId(),
            Content = [new ToolResultBlock(callId, [new TextBlock("a.txt b.txt")])],
            ToolSource = new ToolMessageSource(callId),
        }));
        Append(session, new AssistantMessagePayload(1, 1, new AssistantMessage
        {
            Id = MessageFactory.NewId(),
            Content = [new TextBlock("看完了, 一共两个文件。")],
            ModelSource = new ModelMessageSource("deepseek-official", "deepseek-v4-flash"),
        }, new TokenUsage(1200, 300, CacheReadTokens: 800)));
        Append(session, new DeliverablesPresentedPayload(
            1, callId, [new PresentedFile(Path.Combine(Path.GetTempPath(), "projection-cache-report.md"), "方案")]));
        Append(session, new ApprovalAskedPayload("ask-cache-1", "bash", callId, "需要执行命令", """{"command":"ls"}"""));
        Append(session, new TurnEndPayload(1, new TurnEndReason.Completed()));
    }

    /** 可进表面的三类事件(user/assistant/tool)必须带 surfaceOp, 其余不许带。 */
    private static void Append(Session session, SessionEventPayload payload)
    {
        if (Surface.IsSurfaceEligibleType(payload.Type))
            session.Append(payload, new SurfaceOp.Append());
        else
            session.Append(payload);
    }

    /**
     * 视图模型的可比较形态。排除项都写在注释里: 轨迹时刻是"投影那一刻"的墙上时间, 两次投影不可能相同;
     * 耗时/首 token/吞吐量取决于重放时机器快慢, 同一份日志重放两次都未必一致。
     */
    private static IReadOnlyList<string> Dump(MainViewModel viewModel) =>
    [
        .. viewModel.Messages.Select(message =>
            $"msg|{message.Role}|{message.Kind}|{message.Seq}|{message.RenderSeq}|{message.Time}|{message.Text}|{message.Detail}|"
            + $"{message.ResultText}|{message.HasResult}|{message.IsToolError}|{message.ToolName}|{message.ArgumentText}|"
            + $"{message.IsExpanded}|{message.IsDiffExpanded}|{message.HasDiff}|{message.Feedback}|{message.IsStreaming}|"
            + $"{message.IsSubagentTool}|{message.SubagentLabel}"),
        .. viewModel.TraceItems.Select(trace => $"trace|{trace.Seq}|{trace.Kind}|{trace.Title}|{trace.Detail}|{trace.Message?.Seq}"),
        $"tokens|{viewModel.TokenStats.Totals}|{viewModel.TokenStats.ContextText}|{viewModel.TokenStats.Rounds}|{viewModel.TokenStats.Cache}",
        .. viewModel.Deliverables.Items.Select(item => $"file|{item.Path}|{item.Description}"),
        $"busy|{viewModel.IsBusy}",
    ];
}

