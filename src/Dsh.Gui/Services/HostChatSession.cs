using Avalonia.Threading;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.RemoteHost;

namespace Dsh.Gui.Services;

/**
 * 本机 Host 连不上(握手没完成)。
 * 与"连上了但会话打不开"分开: 启动失败窗口据它决定该不该让用户去查 run/host.port,
 * 两种失败混在一起时用户会被引到一个根本没问题的宿主进程上。
 */
public sealed class HostConnectionUnavailableException(string reason, Exception inner) : Exception(reason, inner);

/** A GUI projection of a Host-owned chat. Disposing detaches the view, never stops the agent. */
public sealed class HostChatSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _subscription;
    /**
     * 整段历史: 大日志走**惰性段**(索引常驻、负载按需), 小日志与老路径是单段物化。
     * 两种形态的列表语义完全一样(条数、逐条序号), 差别只在"取第 i 条时要不要解那一段字节"。
     */
    private SessionEventLog _events = SessionEventLog.FromEvents([]);
    /** 交付物事件: 投影卸载前缀之后, 侧栏仍要能按截断重算, 所以这几条单独留着。 */
    private readonly List<SessionEvent> _deliverableEvents = [];
    /** 交付物是否随折叠批次认出来: 惰性历史为 true(打开时不扫整段), 整段物化时为 false(打开时扫一次)。 */
    private bool _observeBatches;
    /** 投影缓存: 命中时整段历史不必解析, 只补缓存之后的增量事件。 */
    private readonly ProjectionCache? _projections;
    /** 下一条事件的序号。是字段而不是"列表末条 + 1": 列表可能整段没取(投影缓存命中)或被卸载。 */
    private long _nextSeq;
    private long _totalEvents;
    private long _logLength;
    private long _generation;
    private long _navigationGeneration;
    private long _reloadGeneration;
    private IWorkspaceMachine? _followingMachine;
    private string _machine = MachineSpec.LocalName;
    private string _kind = HostSessionKinds.Harness;

    /** 宿主给的整段用户消息时间线: 与"客户端已渲染多少"无关, 时间轴据此画满整条会话。 */
    private IReadOnlyList<RemoteTimelineEntry>? _timeline;

    /** 点开会话到历史到手(宿主读日志 + 分页传到客户端)花了多少毫秒: 用户其实一直在等这一段。 */
    private long _openMs;

    private HostChatSession(
        FleetWorkspace fleet,
        string machine,
        string kind,
        RemoteSessionSummary info,
        RemoteSessionSnapshot snapshot,
        SessionEventLog? log,
        ProjectionSnapshot? cached,
        ProjectionCache? projections,
        string? incomplete)
    {
        Fleet = fleet;
        _machine = machine;
        _kind = kind;
        _projections = projections;
        CachedProjection = cached;
        HistoryIncomplete = incomplete;
        Info = info;
        Header = snapshot.Header;
        ReplaceEvents(snapshot, log);
    }

    public FleetWorkspace Fleet { get; }
    public RemoteSessionSummary Info { get; private set; }
    public SessionHeader Header { get; private set; }
    public SessionId Id => Header.Id;
    public string Machine => _machine;
    public string Kind => _kind;
    public bool IsHarness => string.Equals(_kind, HostSessionKinds.Harness, StringComparison.Ordinal);
    public IReadOnlyList<SessionEvent> Events => _events;

    /** 这段日志一共有多少条事件(界面据此判断"投影是否已经覆盖到末尾", 投影缓存只在这时才是完整的)。 */
    public long TotalEventCount => _totalEvents;
    public IReadOnlyList<RemoteCommandDescriptor> Commands { get; private set; } = [];

    /**
     * 命中投影缓存时的投影本身(消息/轨迹/统计的纯数据形态)。
     * 非 null 意味着**整段历史没有取**: Events 里只有缓存之后的增量, 界面由视图模型还原。
     */
    public ProjectionSnapshot? CachedProjection { get; private set; }

    /**
     * 本次载入没能取全历史时的说明(null 表示整段都在手上)。
     *
     * 会话照样打开: 界面显示已经拿到的那段连续前缀, 并把缺的起点告诉用户 —— 打不开比少一段严重,
     * 悄悄少一段又比说清楚严重。这种状态下也**不写投影缓存**(见 ProjectionCache.SaveAsync)。
     */
    public string? HistoryIncomplete { get; private set; }

    public event Action? Reset;
    public event Action<IReadOnlyList<SessionEvent>>? Received;

    /** 缺口按区间补齐之后到手的迟到事件(序号小于当前已渲染位置): 视图要把它们插回原位。 */
    public event Action<IReadOnlyList<SessionEvent>>? Filled;

    public event Action<string>? Failed;

    public static async Task<HostChatSession> OpenAsync(
        FleetWorkspace fleet,
        string cwd,
        string? sessionId,
        CancellationToken cancellationToken = default,
        ProjectionCache? projections = null)
    {
        // 用户从点击到这一刻一直在等: 这段必须算进"打开"里, 否则大会话的真实代价在打点里看不见。
        var clock = System.Diagnostics.Stopwatch.StartNew();
        fleet.Start();
        WorkspaceHostLease lease;
        try
        {
            lease = await fleet.Find(MachineSpec.LocalName)!.WaitConnectedAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new HostConnectionUnavailableException($"本机 Host 没有响应: {error.Message}", error);
        }
        if (!lease.Client.Supports(SessionEditingProtocol.Capability)
            || !lease.Client.Supports(PluginsProtocol.Capability))
            throw new InvalidOperationException("本机 Host 版本不支持完整 Chat；请升级 dsharp 并重启 Host");
        // 三件事互不依赖(接上会话 / 取历史 / 列命令), 并行发出去: 打开是它们的和, 不是乘积。
        // 只有"没给会话号"时才必须先问出是哪一条。
        RemoteSessionSummary info;
        LoadedHistory loaded;
        IReadOnlyList<RemoteCommandDescriptor> commands;
        if (sessionId is not null)
        {
            var resumeTask = lease.Client.ResumeSessionAsync(sessionId, cancellationToken);
            var loadTask = LoadSnapshotAsync(lease.Client, MachineSpec.LocalName, sessionId, projections, cancellationToken);
            var commandsTask = lease.Client.SessionEditing.ListSessionCommandsAsync(sessionId, cancellationToken);
            info = await resumeTask.ConfigureAwait(false);
            loaded = await loadTask.ConfigureAwait(false);
            commands = await commandsTask.ConfigureAwait(false);
        }
        else
        {
            info = await OpenRecentOrCreateAsync(lease.Client, cwd, cancellationToken).ConfigureAwait(false);
            loaded = await LoadSnapshotAsync(lease.Client, MachineSpec.LocalName, info.Id, projections, cancellationToken).ConfigureAwait(false);
            commands = await lease.Client.SessionEditing.ListSessionCommandsAsync(info.Id, cancellationToken).ConfigureAwait(false);
        }
        var chat = new HostChatSession(
            fleet, MachineSpec.LocalName, HostSessionKinds.Harness, info, loaded.Snapshot, loaded.Log, loaded.Cached, projections,
            loaded.Incomplete);
        chat.Commands = commands;
        chat._openMs = clock.ElapsedMilliseconds;
        return chat;
    }

    /**
     * 打开时带哪些历史: **整段**。
     *
     * 界面上的滚动条量与时间轴刻度都来自"已加载的消息": 少加载一段, 滑块就到不了那段、时间轴的刻度也缺那几条,
     * 用户既滚不过去也跳不过去。所以这里不做"多少条以内整段取"的门槛 —— 整段都取。
     *
     * 取法分三种: 投影缓存命中时只补缓存之后的增量(整段历史不必解析, 这是百万条会话从十几秒降到亚秒的唯一途径);
     * 宿主能把历史当**日志字节**给(SessionLogSliceParser)时只传压缩字节, 客户端自己解析;
     * 否则退回事件快照(老宿主)。
     *
     * 取不全时**照样打开会话**: 已经拿到的连续前缀就是界面内容, 缺的那一段记在 Incomplete 里说清楚。
     * 会话打不开比少一段严重得多, 而"悄悄少一段"又比"明确说少了哪一段"严重 —— 三者按这个顺序取舍。
     */
    private static async Task<LoadedHistory> LoadSnapshotAsync(
        IWorkspaceHost client,
        string machine,
        string id,
        ProjectionCache? projections,
        CancellationToken cancellationToken)
    {
        if (client is not RemoteHostClient paged)
            return new LoadedHistory(await client.GetSessionSnapshotAsync(id, cancellationToken).ConfigureAwait(false), null, null, null);
        // 只取一条事件: 表头 + 日志修订 + 总条数, 代价与历史长度无关, 命中判定靠它。
        var head = await paged.GetSessionSnapshotRangeAsync(id, 0, 1, cancellationToken).ConfigureAwait(false);
        var total = head.TotalEventCount;
        if (await TryRestoreAsync(paged, machine, id, head, projections, cancellationToken).ConfigureAwait(false) is { } restored)
            return restored;
        // 大日志: 只要字节, 不解析。索引来自切片自带的条数, 负载留给折叠按段取。
        if (paged.Supports(HostProtocol.LogSliceCapability)
            && await TryOpenPagedAsync(paged, id, total, cancellationToken).ConfigureAwait(false) is { } opened)
            return opened with { Snapshot = opened.Snapshot with { LogLength = head.LogLength, LogRevision = head.LogRevision } };
        var load = paged.Supports(HostProtocol.LogSliceCapability)
            && await TryLoadHistoricBytesAsync(paged, id, total, cancellationToken).ConfigureAwait(false) is { } sliced
            ? await CompleteWithUnflushedTailAsync(paged, id, sliced, total, cancellationToken).ConfigureAwait(false)
            : await LoadWholeHistoryAsync(paged, head, total, cancellationToken).ConfigureAwait(false);
        // 整段取回来的那份没有修订(切片只带事件): 从表头那一页补上, 供下次写缓存用。
        return new LoadedHistory(
            load.Snapshot with { LogLength = head.LogLength, LogRevision = head.LogRevision }, null, null, load.Incomplete);
    }

    /**
     * 试一次投影缓存: 修订对得上、末事件锚点也对得上, 就只取缓存之后的增量。
     *
     * 锚点是"回退之后又追加到同样长度"的唯一防线: 长度与条数都看不出改写, 但缓存里最后那条事件(时刻+类型)
     * 与当前日志同位置的那条一比就知道 —— 对不上就当没命中, 老老实实整段重放。
     */
    private static async Task<LoadedHistory?> TryRestoreAsync(
        RemoteHostClient client,
        string machine,
        string id,
        RemoteSessionSnapshot head,
        ProjectionCache? projections,
        CancellationToken cancellationToken)
    {
        if (projections is null || head.LogLength <= 0)
            return null;
        if (projections.TryLoad(machine, id, head.LogLength, head.TotalEventCount) is not { } cached)
            return null;
        if (cached.NextSeq > 0)
        {
            var anchor = (await client
                .GetSessionSnapshotRangeAsync(id, cached.NextSeq - 1, 1, cancellationToken)
                .ConfigureAwait(false)).Events.FirstOrDefault();
            if (!ProjectionCache.AnchorMatches(cached, anchor))
                return null;
        }
        var (delta, incomplete) = await FetchRangeAsync(
            client, id, cached.NextSeq, head.TotalEventCount - cached.NextSeq, cancellationToken).ConfigureAwait(false);
        return new LoadedHistory(head with { Events = delta }, null, cached, incomplete);
    }

    /** 一次历史载入的结果: 事件(老路径)或惰性日志(大日志), 外加"为什么没取全"(null 表示整段都在手上)。 */
    private sealed record LoadedHistory(
        RemoteSessionSnapshot Snapshot,
        SessionEventLog? Log,
        ProjectionSnapshot? Cached,
        string? Incomplete);

    /** 一次历史载入的结果: 连续前缀 + "为什么没取全"(null 表示整段都在手上)。 */
    private sealed record HistoryLoad(RemoteSessionSnapshot Snapshot, string? Incomplete);

    /**
     * 日志字节只覆盖**已经落盘**的前缀: 刚追加、还留在落盘批量里的事件磁盘上还没有。
     * 只按切片交付就会把最近的几条吞掉(实测: 压缩检查点消息整条消失), 所以缺的尾部按序号从活会话补上。
     */
    private static async Task<HistoryLoad> CompleteWithUnflushedTailAsync(
        RemoteHostClient client,
        string id,
        RemoteSessionSnapshot sliced,
        long total,
        CancellationToken cancellationToken)
    {
        var next = NextSeqOf(sliced.Events);
        if (next >= total)
            return new HistoryLoad(sliced, null);
        // 前缀必须从 seq 0 起连续; 不连续说明字节这条路给的不是整段历史, 退回事件快照。
        if (sliced.Events.Count > 0 && sliced.Events[0].Seq != 0)
            return await LoadWholeHistoryAsync(client, sliced, total, cancellationToken).ConfigureAwait(false);
        var (tail, incomplete) = await FetchRangeAsync(client, id, next, total - next, cancellationToken).ConfigureAwait(false);
        var events = new List<SessionEvent>(sliced.Events);
        events.AddRange(tail);
        return new HistoryLoad(
            sliced with { Events = events },
            incomplete ?? (events.Count == total ? null : $"会话历史只取到 {events.Count} 条(应有 {total} 条)"));
    }

    /** 一页历史的条数: 只决定一次往返带多少条, 不决定"取多少"。 */
    public const int HistoryPageEvents = 20_000;

    /** 投影一次吃多少条: 只决定"边投影边释放"的粒度, 不决定投影结果。 */
    private const int HistoryBatchEvents = 20_000;

    /** 惰性打开的切片粒度: 一次跳转只需解一页, 所以这一页就是"跳转成本"的单位 —— 页越小跳转越轻, 往返越多。 */
    private const int LazyPageEvents = 2_000;

    /** 同时在飞的取页请求数: 再多只是把线路与宿主的解压线程压满, 不会更快。 */
    private const int PageFetchParallelism = 4;

    /** 排查开关: 置 1 时强制整段物化(与改动前一致), 用于对照与定位"惰性路径是不是这次慢/错的原因"。 */
    private static bool OpenFull
        => Environment.GetEnvironmentVariable("DSH_OPEN_FULL") is { Length: > 0 } value
            && value is not "0" and not "false";

    /**
     * 取整段历史的**日志字节**并本地解析: 按序取页、逐帧解析、按序号拼接。
     *
     * 取页与解析交替进行: 一页到手就解出来接进结果, 之后没人再引用它的字节 —— 在飞与已到未解析的页
     * 永远不超过 PageFetchParallelism 页, 同一时刻已解析的事件也只有一帧那么多, 峰值与"整段同时压在内存里"无关。
     *
     * 任何一段取不回来或解析失败都返回 null: 让调用方退回事件快照这条老路 —— 宁可慢一次,
     * 也不给半段历史(半段历史正是滚动条与时间轴残缺的来源)。
     */
    private static async Task<RemoteSessionSnapshot?> TryLoadHistoricBytesAsync(
        RemoteHostClient client,
        string id,
        long total,
        CancellationToken cancellationToken)
    {
        if (total <= 0)
            return null;
        var pages = (int)((total + HistoryPageEvents - 1) / HistoryPageEvents);
        try
        {
            var accumulated = new SliceAccumulator((int)Math.Min(total, Array.MaxLength));
            var pending = new Queue<Task<RemoteLogSlice>>();
            var started = 0;
            while (started < pages || pending.Count > 0)
            {
                while (started < pages && pending.Count < PageFetchParallelism)
                {
                    var from = (long)started * HistoryPageEvents;
                    pending.Enqueue(client.GetSessionLogSliceAsync(id, from, (int)Math.Min(HistoryPageEvents, total - from), cancellationToken));
                    started += 1;
                }
                if (!accumulated.Append(await pending.Dequeue().ConfigureAwait(false)))
                    return null;
            }
            var first = accumulated.First ?? throw new InvalidOperationException("会话历史的第一段没有取回来");
            return new RemoteSessionSnapshot(first.Header, accumulated.Events, total, first.ApprovalPolicy);
        }
        catch (Exception error) when (error is IOException or StreamJsonRpc.RemoteInvocationException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /**
     * 惰性打开: 只把日志切片搬到本地, **不解析**。
     *
     * 索引由切片自带的 `FromSeq` / `EventCount`(宿主切帧时算出来的)直接拼出来, 于是打开的成本是
     * "搬运压缩字节", 与事件条数无关; 折叠推进到哪一段才解哪一段, 解完的段随 LRU 释放。
     * 落盘批量里还没写下去的尾巴照旧按序号从活会话补上。
     *
     * 任何一个前提不成立(切片不连续、哈希不符、索引没铺满)都返回 null: 退回整段事件快照这条老路。
     * `DSH_OPEN_FULL=1` 可以强制走老路(排查与对照用)。
     */
    private static async Task<LoadedHistory?> TryOpenPagedAsync(
        RemoteHostClient client,
        string id,
        long total,
        CancellationToken cancellationToken)
    {
        // 一页以内的会话本来就只有一段, 拆开没有意义; 超过一页才有"索引 + 按需"可谈。
        if (OpenFull || total <= LazyPageEvents)
            return null;
        var slices = await FetchSlicesAsync(client, id, total, cancellationToken).ConfigureAwait(false);
        if (slices is not { Count: > 0 } || slices[0].FromSeq != 0)
            return null;
        var covered = slices[^1].FromSeq + slices[^1].EventCount;
        // 索引以"真正到手多少条"为准, 不以宿主的 TotalEventCount 为准: 后者陈旧时会把多出来的那几条悄悄截掉,
        // 而索引与日志错位正是后续所有跳号的源头。取不全时照样建索引(缺口由 Coverage 报出来、按区间补),
        // 只是把它如实说出来, 而不是退回整段物化。
        if (SessionEventLog.TryFromSlices(slices, covered) is not { } log)
            return null;
        string? incomplete = covered > total
            ? $"宿主报的总数是 {total} 条, 实际取到 {covered} 条"
            : null;
        if (covered < total)
        {
            var (tail, gap) = await FetchRangeAsync(client, id, covered, total - covered, cancellationToken).ConfigureAwait(false);
            log.Append(tail);
            incomplete = gap ?? (log.Count == total ? null : $"会话历史只取到 {log.Count} 条(应有 {total} 条)");
        }
        // 切片之间也可能有空档(某一段取不回来): 索引照样能用, 只是要把缺口报出来。
        if (incomplete is null && Holes(log.Coverage, total) is { Count: > 0 } holes)
            incomplete = $"会话历史缺 {holes.Sum(hole => hole.Count)} 条(从 #{holes[0].Start} 起)";
        var first = slices[0];
        return new LoadedHistory(
            new RemoteSessionSnapshot(first.Header, [], total, first.ApprovalPolicy), log, null, incomplete);
    }

    /**
     * 取整段切片: 先按固定步长并行把请求发出去(往返是主要成本), 再按序号拼起来。
     *
     * 宿主一页有字节预算, 被截短的一页只覆盖到一半: 于是下一页的起点会落在已覆盖区间之后,
     * 中间那几条要**从缺口起点补取**, 否则索引里就有一个洞(而索引有洞只能用整段快照)。
     * 相邻页还会重叠(帧边界只落在行边界上), 重叠由 SessionEventLog 按段起点裁掉。
     */
    private static async Task<List<RemoteLogSlice>?> FetchSlicesAsync(
        RemoteHostClient client,
        string id,
        long total,
        CancellationToken cancellationToken)
    {
        var pages = (int)((total + LazyPageEvents - 1) / LazyPageEvents);
        var fetched = new RemoteLogSlice?[pages];
        try
        {
            await Parallel.ForAsync(0, pages,
                new ParallelOptions { MaxDegreeOfParallelism = PageFetchParallelism, CancellationToken = cancellationToken },
                async (page, token) =>
                {
                    var from = (long)page * LazyPageEvents;
                    fetched[page] = await client
                        .GetSessionLogSliceAsync(id, from, (int)Math.Min(LazyPageEvents, total - from), token)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);
            var slices = new List<RemoteLogSlice>(pages);
            long covered = 0;
            foreach (var page in fetched)
            {
                if (page is null || !Verified(page))
                    return null;
                while (covered < page.FromSeq)
                {
                    var gap = await client
                        .GetSessionLogSliceAsync(id, covered, (int)Math.Min(LazyPageEvents, page.FromSeq - covered), cancellationToken)
                        .ConfigureAwait(false);
                    if (!Verified(gap) || gap.EventCount <= 0)
                        return null;
                    slices.Add(gap);
                    var advanced = gap.FromSeq + gap.EventCount;
                    if (advanced <= covered)
                        return null;
                    covered = advanced;
                }
                if (page.FromSeq + page.EventCount <= covered)
                    continue;
                slices.Add(page);
                covered = page.FromSeq + page.EventCount;
            }
            return slices.Count > 0 ? slices : null;
        }
        catch (Exception error) when (error is IOException or StreamJsonRpc.RemoteInvocationException or FormatException or InvalidOperationException)
        {
            return null;
        }

        // 切片不可变, 内容哈希能当场验明: 传输/宿主出了问题就退回整段快照, 不拿坏字节去投影。
        static bool Verified(RemoteLogSlice slice)
            => slice.ContentHash.Length == 0 || SessionLogSlice.HashOf(slice.Bytes) == slice.ContentHash;
    }

    /**
     * 按序号把一页页切片接成整段事件。
     *
     * 帧边界只落在行边界上, 相邻片段可能多带几条: 只收还没收到的那部分, 并按序号保证连续。
     * 一页解完就不再持有它的字节(切片不可变, 不复用), 于是常驻的中间量只有一页的事件。
     */
    private sealed class SliceAccumulator(int capacity)
    {
        private long _expected;

        public List<SessionEvent> Events { get; } = new(capacity);
        public RemoteLogSlice? First { get; private set; }

        /** 接上一页; false 表示这一页不能收(取不回/哈希不符/序号不连续), 调用方退回事件快照。 */
        public bool Append(RemoteLogSlice? slice)
        {
            if (slice is null)
                return false;
            // 切片不可变, 内容哈希能当场验明: 传输/宿主出了问题就退回事件快照, 不拿坏字节去投影。
            if (slice.ContentHash.Length > 0 && SessionLogSlice.HashOf(slice.Bytes) != slice.ContentHash)
                return false;
            First ??= slice;
            foreach (var batch in SessionLogSliceParser.ParseBatches(slice.Bytes, slice.FromSeq))
            {
                foreach (var sessionEvent in batch)
                {
                    if (sessionEvent.Seq < _expected)
                        continue;
                    if (sessionEvent.Seq > _expected)
                        return false;
                    Events.Add(sessionEvent);
                    _expected = sessionEvent.Seq + 1;
                }
            }
            return true;
        }
    }

    /**
     * 取齐整段历史: 从 #0 起按序号续页, 表头/策略取自表头那一页。
     *
     * 首页也走同一个续页器 —— 一页被字节预算截断时它同样只给前半段, 直接当"整段历史"就是少一段。
     * 结果以"从 #0 起的连续前缀"交付; 取不全时把原因带回去(会话照常打开并说明少了哪一段),
     * 而不是让会话打不开 —— 打不开的代价比少一段大得多。
     */
    private static async Task<HistoryLoad> LoadWholeHistoryAsync(
        RemoteHostClient client,
        RemoteSessionSnapshot head,
        long total,
        CancellationToken cancellationToken)
    {
        var (events, incomplete) = await FetchRangeAsync(client, id: head.Header.Id.Value, 0, total, cancellationToken)
            .ConfigureAwait(false);
        var reason = incomplete;
        if (reason is null && events.Count > 0 && events[0].Seq != 0)
            reason = $"宿主只交付了窗口内的一段(从 #{events[0].Seq} 起, 共 {total} 条)";
        if (reason is null && events.Count != total)
            reason = $"会话历史只取到 {events.Count} 条(应有 {total} 条)";
        return new HistoryLoad(head with { Events = events }, reason);
    }

    /** 从 fromEvent 起取 count 条事件, **按已到事件的序号**续页, 直到恰好取满。 */
        /**
         * 续页只看"已经拿到哪一条", 不看页号。
         *
         * 宿主一页还有 12MB 字节预算(RemoteSnapshotPage): 一页中途被截断时它给的是**更短的一页**,
         * 而不是"下一段从这里开始"。按页号(页 × 固定条数)续页就会静默跳过被截掉的那几条 ——
         * 实测 887,072 条的会话因此丢了 477,300..479,999, 客户端随后把它当成跳号, 整个界面停在 53.8%。
         *
         * 所以: 每页必须从 expected 起; 拿到之后 expected = 末条序号 + 1; 空页或起点靠后都当场补取,
         * 补不上就报错(宁可失败, 不给半段历史)。
         */
        /**
     * 续页只看"已经拿到哪一条", 不看页号。
     *
     * 宿主一页还有 12MB 字节预算(RemoteSnapshotPage): 一页中途被截断时它给的是**更短的一页**,
     * 而不是"下一段从这里开始"。按页号(页 × 固定条数)续页就会静默跳过被截掉的那几条 ——
     * 实测 887,072 条的会话因此丢了 477,300..479,999, 客户端随后把它当成跳号, 整个界面停在 53.8%。
     *
     * 所以: 每页必须从 expected 起; 拿到之后 expected = 末条序号 + 1; 起点靠后就当场补取。
     * 补不上时**不抛异常**(会话打不开比少一段严重), 而是带着"从哪一条开始缺"的说明返回已拿到的连续前缀。
     */
    private static async Task<(IReadOnlyList<SessionEvent> Events, string? Incomplete)> FetchRangeAsync(
        RemoteHostClient client,
        string id,
        long fromEvent,
        long count,
        CancellationToken cancellationToken)
    {
        var events = new List<SessionEvent>((int)Math.Min(Math.Max(count, 0), Array.MaxLength));
        if (count <= 0)
            return (events, null);
        var end = fromEvent + count;
        var pages = (int)((count + HistoryPageEvents - 1) / HistoryPageEvents);
        var fetched = new RemoteSessionSnapshot?[pages];
        // 先按固定步长并行把请求发出去(往返是主要成本), 拼接阶段再按序号补齐截断造成的缺口。
        await Parallel.ForAsync(0, pages,
            new ParallelOptions { MaxDegreeOfParallelism = PageFetchParallelism, CancellationToken = cancellationToken },
            async (page, token) =>
            {
                var offset = (long)page * HistoryPageEvents;
                fetched[page] = await client.GetSessionSnapshotRangeAsync(
                    id, fromEvent + offset, (int)Math.Min(HistoryPageEvents, count - offset), token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        long expected = fromEvent;
        string? incomplete = null;
        foreach (var page in fetched)
        {
            if (page is null)
            {
                incomplete = $"#{expected} 起的一页没有取回来";
                break;
            }
            if (await AppendFromAsync(page.Events).ConfigureAwait(false) is { } gap)
            {
                incomplete = gap;
                break;
            }
        }
        while (incomplete is null && expected < end)
        {
            var page = await ReadPageAsync(expected, (int)Math.Min(HistoryPageEvents, end - expected)).ConfigureAwait(false);
            incomplete = await AppendFromAsync(page).ConfigureAwait(false);
        }
        return (events, incomplete);

        // 把一页里"还没收到的那部分"接上; 起点比 expected 靠后说明前一段被截断了, 先补齐缺口。
        async Task<string?> AppendFromAsync(IReadOnlyList<SessionEvent> received)
        {
            // 空页意味着"这一段再也取不回来了": 立刻收工, 否则外层会一直重试同一页(实测能把界面卡死几分钟)。
            if (received.Count == 0)
                return $"会话历史从 #{expected} 起的一页是空的";
            var index = 0;
            while (index < received.Count)
            {
                var next = received[index];
                if (next.Seq < expected)
                {
                    index += 1;
                    continue;
                }
                if (next.Seq > expected)
                {
                    var want = (int)Math.Min(HistoryPageEvents, next.Seq - expected);
                    var filled = await ReadPageAsync(expected, want).ConfigureAwait(false);
                    if (filled.Count == 0 || filled[0].Seq != expected)
                    {
                        return $"会话历史从 #{expected} 起取不回来(宿主给的是 #{(filled.Count == 0 ? "空页" : filled[0].Seq.ToString())})";
                    }
                    events.AddRange(filled);
                    expected += filled.Count;
                    continue;
                }
                events.Add(next);
                expected += 1;
                index += 1;
            }
            return null;
        }

        async Task<IReadOnlyList<SessionEvent>> ReadPageAsync(long offset, int want)
        {
            if (want <= 0)
                return [];
            var page = await client.GetSessionSnapshotRangeAsync(id, offset, want, cancellationToken).ConfigureAwait(false);
            return page.Events;
        }
    }

    /** 打开后本地的事件覆盖整段会话: 首条序号是 0 (或空会话)。界面据此判断"历史是否完整"。 */
    public bool HasWholeHistory => _events.Count == 0 || _events[0].Seq == 0;

    /** 下一条事件的序号。序号是全局的, 而事件列表可能不是从 0 开始(窗口化/分页快照), 所以条数不能当序号用。 */
    private static long NextSeqOf(IReadOnlyList<SessionEvent> events)
        => events.Count == 0 ? 0 : events[^1].Seq + 1;

    /**
     * 未显式指定会话时恢复同工作区最近一次会话, 没有可恢复的才新建。
     * 只看目录不看 Summary: 磁盘上的会话由 Host 用 SessionHeader 投影, Summary 恒为 null,
     * 只有活着且已有用户消息的会话才有摘要 —— 拿摘要当筛选条件, Host 一重启就永远选不中,
     * 于是每次启动都新建会话(空会话又因为没事件而不在列表里, 下次启动照样新建)。
     */
    private static async Task<RemoteSessionSummary> OpenRecentOrCreateAsync(
        IWorkspaceHost client,
        string cwd,
        CancellationToken cancellationToken)
    {
        var recent = (await client.ListSessionsAsync(cancellationToken))
            .Where(session => SameDirectory(session.Cwd, cwd))
            .OrderByDescending(session => session.UpdatedAt)
            .FirstOrDefault();
        if (recent is not null)
            return await client.ResumeSessionAsync(recent.SessionId, cancellationToken);
        // 还没写过事件的新会话不落盘也不进 ListSessionsAsync, 但它仍是 Host 里活着的那一个: 复用它, 不再新建。
        var pending = (await client.ListHostSessionsAsync(cancellationToken))
            .Where(session => session.Kind == HostSessionKinds.Harness
                && session.Status != HostSessionStatuses.Exited
                && SameDirectory(session.Cwd, cwd))
            .OrderByDescending(session => session.UpdatedAt)
            .FirstOrDefault();
        return pending is not null
            ? await client.ResumeSessionAsync(pending.SessionId, cancellationToken)
            : await client.CreateSessionAsync(cwd, cancellationToken);
    }

    private static bool SameDirectory(string? path, string cwd)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(cwd),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Follow()
    {
        _subscription?.Cancel();
        _subscription = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var generation = ++_generation;
        _followingMachine = Fleet.Find(_machine)!;
        _ = FollowAsync(_followingMachine, Info.Id, generation, _subscription.Token);
    }

    public void RefreshConnection()
    {
        if (!_lifetime.IsCancellationRequested && !ReferenceEquals(_followingMachine, Fleet.Find(_machine)))
            Follow();
    }

    /** 订阅起点: 本地已有的下一条事件。用序号而不是条数: 窗口化/分页快照的列表不从 0 开始。 */
    private long NextSeq => _nextSeq;

    private async Task FollowAsync(IWorkspaceMachine machine, string id, long generation, CancellationToken cancellationToken)
    {
        var events = new DispatcherBridge<SessionEvent>(batch =>
        {
            if (generation != _generation || cancellationToken.IsCancellationRequested)
                return;
            foreach (var sessionEvent in batch)
            {
                if (sessionEvent.Seq < _nextSeq)
                    continue;
                _events.Append([sessionEvent]);
                _nextSeq = sessionEvent.Seq + 1;
            }
            ObserveDeliverables(batch);
            Received?.Invoke(batch);
        });
        try
        {
            await foreach (var item in machine.SubscribeAsync(id, NextSeq, cancellationToken))
            {
                if (item.Reset)
                {
                    await ReloadAsync(cancellationToken);
                    continue;
                }

                if (item.Event is { } sessionEvent)
                    events.Enqueue(sessionEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() => { if (generation == _generation && !_lifetime.IsCancellationRequested) Failed?.Invoke(error.Message); });
        }
    }

    public async Task<IWorkspaceHost> ClientAsync(CancellationToken cancellationToken = default)
        => (await Fleet.Find(_machine)!.WaitConnectedAsync(cancellationToken)).Client;

    public async Task SwitchAsync(string id, CancellationToken cancellationToken = default)
        => await SwitchToAsync(_machine, id, _kind, cancellationToken);

    public async Task SwitchToAsync(
        string machine,
        string id,
        string kind,
        CancellationToken cancellationToken = default)
    {
        var navigation = ++_navigationGeneration;
        var connection = Fleet.Find(machine) ?? throw new InvalidOperationException($"no machine named \"{machine}\"");
        var client = (await connection.WaitConnectedAsync(cancellationToken).ConfigureAwait(false)).Client;
        var info = kind == HostSessionKinds.Harness
            ? await client.ResumeSessionAsync(id, cancellationToken).ConfigureAwait(false)
            : ToInfo(await ResumeAgentAsync(connection, client, id, cancellationToken).ConfigureAwait(false));
        var snapshotTask = LoadSnapshotAsync(client, machine, id, _projections, cancellationToken);
        var commandsTask = kind == HostSessionKinds.Harness
            ? client.SessionEditing.ListSessionCommandsAsync(id, cancellationToken)
            : Task.FromResult<IReadOnlyList<RemoteCommandDescriptor>>([]);
        await Task.WhenAll(snapshotTask, commandsTask).ConfigureAwait(false);
        var loaded = await snapshotTask.ConfigureAwait(false);
        var snapshot = loaded.Snapshot;
        var commands = await commandsTask.ConfigureAwait(false);
        var header = snapshot.Header;
        var events = snapshot.Events;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (navigation != _navigationGeneration || _lifetime.IsCancellationRequested)
                return;
            _subscription?.Cancel();
            ++_generation;
            _machine = machine;
            _kind = kind;
            Info = info;
            Header = header;
            CachedProjection = loaded.Cached;
            HistoryIncomplete = loaded.Incomplete;
            _totalEvents = snapshot.TotalEventCount;
            _logLength = snapshot.LogLength;
            _events = loaded.Log ?? SessionEventLog.FromEvents(events);
            _observeBatches = loaded.Log is not null;
            if (!_observeBatches)
                ObserveDeliverables(events);
            _nextSeq = Math.Max(loaded.Cached?.NextSeq ?? 0, NextSeqOf(_events));
            Commands = commands;
            Reset?.Invoke();
            Follow();
        });
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
        => ReloadAsync(false, cancellationToken);

    public async Task ReloadAsync(bool forceReset, CancellationToken cancellationToken = default)
    {
        var id = Info.Id;
        var generation = _generation;
        var reload = ++_reloadGeneration;
        var loaded = await LoadSnapshotAsync(
            await ClientAsync(cancellationToken).ConfigureAwait(false), _machine, id, _projections, cancellationToken).ConfigureAwait(false);
        var snapshot = loaded.Snapshot;
        var header = snapshot.Header;
        var replacement = snapshot.Events;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _generation || reload != _reloadGeneration || _lifetime.IsCancellationRequested)
                return;
            var titleChanged = !string.Equals(Header.Title, header.Title, StringComparison.Ordinal);
            Header = header;
            HistoryIncomplete = loaded.Incomplete;
            // 缓存命中的会话上重载(切模型/推理档位/重命名)通常只多了尾部事件:
            // 前缀没变就按增量接上, 不重建整个消息/视觉树(长会话每次选择都要付一遍 O(历史) 的布局)。
            if (!forceReset && !titleChanged && CachedProjection is { } restored
                && loaded.Cached is { NextSeq: var nextSeq } cached && nextSeq == restored.NextSeq
                && (replacement.Count == 0 || replacement[0].Seq >= _nextSeq))
            {
                CachedProjection = cached;
                var tail = replacement.Where(sessionEvent => sessionEvent.Seq >= _nextSeq).ToList();
                if (tail.Count == 0)
                    return;
                _events.Append(tail);
                _nextSeq = Math.Max(_nextSeq, NextSeqOf(_events));
                ObserveDeliverables(tail);
                Received?.Invoke(tail);
                return;
            }
            // 命令通常只在尾部追加事件(例如 /model、/reasoning、/preset)。全量 ReplaceEvents + Reset 会清空并重建
            // 整个消息/视觉树, 长会话每次选择都要付一遍 O(历史) 的布局与样式分配。
            if (forceReset || titleChanged || !TryAppend(replacement, out var appended))
            {
                // 整段重取了: 之前由缓存还原的投影不再有意义。
                CachedProjection = null;
                ReplaceEvents(snapshot, loaded.Log);
                Reset?.Invoke();
                return;
            }

            if (appended.Count == 0)
                return;
            _events.Append(appended);
            _nextSeq = Math.Max(_nextSeq, NextSeqOf(_events));
            ObserveDeliverables(appended);
            Received?.Invoke(appended);
        });
    }

    /** 本地索引已经覆盖到的序号区间; 区间之间就是缺口。 */
    public IReadOnlyList<SeqRange> Coverage => _events.Coverage;

    /** 撤回之后: 旧投影里还留着被撤掉的内容, 不能再当"下次打开的样子"。 */
    public void ForgetProjection()
    {
        _projections?.Drop(_machine, Info.Id);
        CachedProjection = null;
    }

    /** 覆盖区间之间的空位(0 到 total 之间没被覆盖的部分)。 */
    private static List<SeqRange> Holes(IReadOnlyList<SeqRange> coverage, long total)
    {
        var holes = new List<SeqRange>();
        var cursor = 0L;
        foreach (var range in coverage)
        {
            if (range.Start > cursor)
                holes.Add(new SeqRange(cursor, range.Start));
            cursor = Math.Max(cursor, range.End);
        }
        if (total > cursor)
            holes.Add(new SeqRange(cursor, total));
        return holes;
    }

    /**
     * 按区间补齐缺口: 只取缺的那几条, 取回后插进本地索引并通知视图。
     *
     * 与"整段重取"的区别是代价: 缺口通常几十到几千条, 而整段是几十万到百万条 ——
     * 后者还要把已经渲染好的界面全部作废重建。
     */
    /** 会话整段的用户消息时间线(宿主扫日志/内存得出), 同一份会话内容上复用结果。 */
    public async Task<IReadOnlyList<RemoteTimelineEntry>> TimelineAsync(CancellationToken cancellationToken = default)
    {
        if (_timeline is { } cached)
            return cached;
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        var entries = await client.SessionEditing.SessionTimelineAsync(Id.Value, cancellationToken).ConfigureAwait(false);
        _timeline = entries;
        return entries;
    }

    /** 打开这一段(宿主读日志 + 分页传输)的耗时; 探针用它把'点击→首屏'接完整。 */
    public long OpenMs => _openMs;

    public async Task<bool> FillAsync(SeqRange gap, CancellationToken cancellationToken = default)
    {
        if (gap.IsEmpty)
            return false;
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        if (client is not RemoteHostClient paged)
            return false;
        var (events, _) = await FetchRangeAsync(paged, Info.Id, gap.Start, gap.Count, cancellationToken).ConfigureAwait(false);
        if (events.Count == 0)
            return false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_lifetime.IsCancellationRequested)
                return;
            _events.Fill(events);
            Filled?.Invoke(events);
        });
        return true;
    }

    /**
     * 回头取指定的几条事件, **不并入本地索引**: 投影缓存命中时整段历史都没有取, 只有"重建某张卡片"
     * 这类局部需要才回头读一次。并进索引反而会让重放把同一条消息再渲染一遍。
     *
     * 取不到的序号不在结果里(远端机器、宿主不支持按区间取), 调用方据此停在手上已有的内容上。
     */
    public async Task<IReadOnlyDictionary<long, SessionEvent>> PeekAsync(
        IReadOnlyList<long> seqs,
        CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<long, SessionEvent>(seqs.Count);
        if (seqs.Count == 0)
            return found;
        if (await ClientAsync(cancellationToken).ConfigureAwait(false) is not RemoteHostClient paged)
            return found;
        foreach (var seq in seqs.Distinct())
        {
            var (events, _) = await FetchRangeAsync(paged, Info.Id, seq, 1, cancellationToken).ConfigureAwait(false);
            foreach (var single in events)
            {
                if (single.Seq == seq)
                    found[seq] = single;
            }
        }
        return found;
    }

    /** 快照只是旧事件列表的尾部追加时才返回 true; 截断/重写会话仍需整屏重建。 */
    private bool TryAppend(IReadOnlyList<SessionEvent> replacement, out List<SessionEvent> appended)
    {
        appended = [];
        // 投影缓存命中时本地只有增量事件, "列表是整段前缀"这个前提不成立。
        if (CachedProjection is not null)
            return false;
        if (replacement.Count < _events.Count)
            return false;
        for (var index = 0; index < _events.Count; index++)
        {
            if (replacement[index].Seq != _events[index].Seq)
                return false;
        }

        appended = [.. replacement.Skip(_events.Count)];
        return true;
    }

    private void ReplaceEvents(RemoteSessionSnapshot snapshot, SessionEventLog? log)
    {
        _events = log ?? SessionEventLog.FromEvents(snapshot.Events);
        _deliverableEvents.Clear();
        // 内容换了一份(重开/重同步/回退): 宿主给的时间线可能已经变了, 下次用时重新问。
        _timeline = null;
        // 惰性历史不在打开时扫一遍(那会把每一段都解出来, 等于放弃惰性): 交付物改由折叠批次逐个认出来,
        // 两条路径各扫一次, 不能都扫 —— 都扫会让同一条交付物在侧栏出现两遍。
        _observeBatches = log is not null;
        if (!_observeBatches)
            ObserveDeliverables(snapshot.Events);
        _totalEvents = Math.Max(snapshot.TotalEventCount, CachedProjection?.TotalEvents ?? 0);
        _logLength = Math.Max(snapshot.LogLength, CachedProjection?.LogLength ?? 0);
        // 游标只认本地索引的最后一条: 宿主的 TotalEventCount 可能比真正到手的多(陈旧、或窗口化只给了一段),
        // 拿它当游标会把"下一条事件"钉在一个永远取不到的序号上, 后续实时事件全部变成跳号。
        _nextSeq = Math.Max(CachedProjection?.NextSeq ?? 0, NextSeqOf(_events));
    }

    /**
     * 整段历史**按批**交给投影: 每交一批, 投影消费完就把这一批之前的前缀放掉。
     *
     * 视图只在打开会话时把历史整段过一遍(ShowSession), 之后只用尾部实时流; 那些已投影的事件对象
     * 没有陪着等整段投影跑完的理由 —— 于是峰值只与"最后一批 + 最后一个回合"有关, 而不是整段。
     * 真需要更早的事件时按日志字节重取(切片比传事件便宜一个数量级)。
     *
     * 保留的起点取**最后一个回合的起点**而不是"最后多少条": 截断/重放都按回合进行, 那是个语义边界。
     * 最终状态与"整段投影完再统一释放前缀"完全一致, 差别只在于释放发生在投影推进的过程中。
     */
    public IEnumerable<IReadOnlyList<SessionEvent>> HistoryBatches()
    {
        var next = 0;
        // 保留的起点就在**这一遍折叠里**认: 每个事件都会被走一次, 回头再扫一遍只会在惰性历史上
        // 反复解码同一批段(实测 131MB 的会话因此把投影从 7s 拖到 48s)。
        var turnStart = -1;
        while (next < _events.Count)
        {
            var count = Math.Min(HistoryBatchEvents, _events.Count - next);
            var batch = _events.Slice(next, count);
            // 惰性历史里交付物是这一批批认出来的, 所以扫描放在这里, 打开时不扫整段。
            if (_observeBatches)
                ObserveDeliverables(batch);
            for (var offset = 0; offset < batch.Count; offset += 1)
            {
                if (batch[offset].Data is TurnStartPayload)
                    turnStart = next + offset;
            }
            next += count;
            yield return batch;
            // 0 表示"还没有回合起点"(没有可放的前缀), 与逐条回收之前的判据一致。
            if (turnStart <= 0)
                continue;
            _events.RemovePrefix(turnStart);
            next -= turnStart;
            turnStart = 0;
            HistoryReleased = true;
        }
    }

    /** 历史前缀是否已被释放(需要更早的事件时要重取)。 */
    public bool HistoryReleased { get; private set; }

    /**
     * 当前投影覆盖到的日志修订: 视图把投影抓成快照之后由它标注"这份快照覆盖到哪条事件"。
     * 末事件的时刻与类型是缓存命中时的锚点(见 ProjectionCache.AnchorMatches)。
     */
    public ProjectionAnchor AnchorFor(long nextSeq)
    {
        if (LastEventBefore(nextSeq) is { } last)
            return new ProjectionAnchor(nextSeq, _totalEvents, _logLength, last.Time, last.Type);
        return new ProjectionAnchor(nextSeq, _totalEvents, _logLength, CachedProjection?.LastEventTime, CachedProjection?.LastEventType);
    }

    private SessionEvent? LastEventBefore(long nextSeq)
    {
        for (var index = _events.Count - 1; index >= 0; index -= 1)
        {
            if (_events[index].Seq == nextSeq - 1)
                return _events[index];
        }
        return null;
    }

    /** 存下投影快照。缓存是派生数据: 写不进去只是下次慢一次, 不影响会话本身。 */
    public Task SaveProjectionAsync(ProjectionSnapshot snapshot)
        => _projections?.SaveAsync(_machine, Id.Value, snapshot) ?? Task.CompletedTask;

    /** 交付物事件: 只留这几条, 卸载前缀之后侧栏还能按截断重算。 */
    public IReadOnlyList<SessionEvent> DeliverableEvents => _deliverableEvents;

    private void ObserveDeliverables(IReadOnlyList<SessionEvent> events)
    {
        foreach (var sessionEvent in events)
        {
            if (sessionEvent.Data is DeliverablesPresentedPayload)
                _deliverableEvents.Add(sessionEvent);
        }
    }

    public async Task RefreshCommandsAsync()
    {
        if (!IsHarness)
        {
            Commands = [];
            return;
        }
        var id = Info.Id;
        var commands = await (await ClientAsync(_lifetime.Token)).SessionEditing.ListSessionCommandsAsync(id, _lifetime.Token);
        if (Info.Id == id && !_lifetime.IsCancellationRequested)
            Commands = commands;
    }

    public async Task<RemoteCommandResult> ExecuteAsync(string line)
    {
        if (!IsHarness)
            return new RemoteCommandResult(false, null, null);
        var result = await (await ClientAsync(_lifetime.Token)).SessionEditing.ExecuteSessionCommandAsync(Info.Id, line, _lifetime.Token);

        return result;
    }

    public async Task<string> RunAsync(string line)
    {
        var result = await ExecuteAsync(line);
        return result.Known ? result.Text ?? "" : $"未知命令: {line}";
    }

    public async Task SendAsync(string text, IReadOnlyList<RemoteImageBlock>? images = null)
    {
        var client = await ClientAsync(_lifetime.Token);
        if (!IsHarness)
        {
            await client.Agent.PromptAgentAsync(Info.Id, text, _lifetime.Token);
            return;
        }
        await client.ResumeSessionAsync(Info.Id, _lifetime.Token);
        await client.SendMessageAsync(Info.Id, text, images ?? [], _lifetime.Token);
    }

    private static async Task<RemoteSessionSummary> ResumeAgentAsync(
        IWorkspaceMachine connection,
        IWorkspaceHost client,
        string id,
        CancellationToken cancellationToken)
    {
        var current = connection.Sessions.FirstOrDefault(session => string.Equals(session.SessionId, id, StringComparison.Ordinal));
        if (current is { Status: not HostSessionStatuses.Exited })
            return current;
        return await client.Agent.ResumeAgentAsync(id, cancellationToken);
    }

    private static RemoteSessionSummary ToInfo(RemoteSessionSummary session)
        => new(
            session.SessionId,
            session.ProjectPath ?? "",
            session.Status,
            session.CreatedAt,
            session.Title,
            null,
            session.LastActivityAt);

    public void Dispose()
    {
        ++_generation;
        _lifetime.Cancel();
        _subscription?.Cancel();
    }
}


