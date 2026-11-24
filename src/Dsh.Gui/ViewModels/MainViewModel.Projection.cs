using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Gui.ViewModels;

/**
 * 投影的抓取与恢复(阶段 3): 把 ShowSession 重放出来的界面状态整段存下来, 下次打开直接还原。
 *
 * 这里的每个字段都必须与 ApplyEvent 的副作用严格对应 —— 少一个, 表现就是"重开之后少一段/样式不同"。
 * 因此两边共用同一批构造函数与同一个折叠规则(交付物走 SessionDeliverables, 轨迹过滤走 RefreshTraceView),
 * 只有确实无法确定的那几项(轨迹时刻由投影时刻决定)才原样存下来。
 */
public sealed partial class MainViewModel
{
    /**
     * 把当前投影整段存进缓存(ShowSession 结束后调用)。
     * 存的是"当前界面状态 + 它覆盖到的日志修订": 修订没变的下次打开就不必再解析重放。
     *
     * 命中缓存且之后没有新事件时**不重写**: 抓快照要遍历整个投影(UI 线程, 单次实测 940–1356MB 分配),
     * 编码约 1s, 写盘 5.6MB —— 而磁盘上那份覆盖的正是同一条末事件, 写下去的只是同样的内容 + 新的 mtime。
     * 判据全部来自缓存自己的字段: 渲染到哪儿(NextSeq)、日志修订(总条数/字节数)、末事件锚点。
     */
    internal Task SaveProjectionAsync()
    {
        var anchor = _chat.AnchorFor(_renderedSeq);
        if (_chat.CachedProjection is { } cached && CoversSameRevision(cached, anchor))
            return Task.CompletedTask;
        return _chat.SaveProjectionAsync(CaptureProjection(anchor));
    }

    /** 磁盘上那份缓存覆盖的修订与锚点, 与这次渲染到达的位置完全一致 ⇒ 再存一次也存不出新东西。 */
    private static bool CoversSameRevision(ProjectionSnapshot cached, ProjectionAnchor anchor)
        => anchor.NextSeq == cached.NextSeq
            && anchor.TotalEvents == cached.TotalEvents
            && anchor.LogLength == cached.LogLength
            && anchor.LastEventTime == cached.LastEventTime
            && string.Equals(anchor.LastEventType, cached.LastEventType, StringComparison.Ordinal);

    /** 抓取当前投影。anchor 是这份投影覆盖到的日志修订(由 HostChatSession 提供)。 */
    internal ProjectionSnapshot CaptureProjection(ProjectionAnchor anchor)
    {
        var positions = new Dictionary<MessageViewModel, int>(Messages.Count, ReferenceEqualityComparer.Instance);
        for (var index = 0; index < Messages.Count; index++)
            positions[Messages[index]] = index;
        var diffs = new List<DiffCardSnapshot>();
        var messages = new List<MessageSnapshot>(Messages.Count);
        foreach (var message in Messages)
        {
            int? diff = null;
            if (message.Diff is { } card)
            {
                diff = diffs.Count;
                diffs.Add(DiffCardSnapshot.FromCard(card));
            }
            messages.Add(new MessageSnapshot
            {
                Role = message.Role,
                Kind = (int)message.Kind,
                Text = message.Text,
                Seq = message.Seq,
                RenderSeq = message.RenderSeq,
                Time = message.Time,
                Detail = message.Detail,
                IsExpanded = message.IsExpanded,
                IsStreaming = message.IsStreaming,
                Feedback = (int)message.Feedback,
                ToolName = message.ToolName,
                ArgumentText = message.ArgumentText,
                ResultText = message.ResultText,
                HasResult = message.HasResult,
                IsToolError = message.IsToolError,
                IsDiffExpanded = message.IsDiffExpanded,
                IsSubagentTool = message.IsSubagentTool,
                SubagentLabel = message.SubagentLabel,
                SubagentSessionId = message.SubagentSessionId?.Value,
                Diff = diff,
                DiffResultSeq = message.DiffSourceSeq,
            });
        }
        var trace = new List<TraceSnapshot>(TraceItems.Count);
        foreach (var item in TraceItems)
        {
            trace.Add(new TraceSnapshot
            {
                Seq = item.Seq,
                Kind = (int)item.Kind,
                Title = item.Title,
                Detail = item.Detail,
                Time = item.Time,
                Message = item.Message is { } target && positions.TryGetValue(target, out var index) ? index : null,
            });
        }
        return new ProjectionSnapshot
        {
            FormatVersion = ProjectionSnapshot.CurrentFormatVersion,
            NextSeq = anchor.NextSeq,
            TotalEvents = anchor.TotalEvents,
            LogLength = anchor.LogLength,
            LastEventTime = anchor.LastEventTime,
            LastEventType = anchor.LastEventType,
            RenderedAny = _renderedAny,
            IsBusy = IsBusy,
            CompactionBusy = _compactionBusy,
            RequestHeader = _requestHeader is { } header
                ? new LlmHeaderSnapshot(
                    header.Provider, header.Model, header.ReasoningEffort?.Value, header.Temperature, header.MaxTokens, header.Stop)
                : null,
            Messages = messages,
            Trace = trace,
            LastUserMessageSeqs = [.. _lastUserMessages.Select(message => message.Seq ?? -1)],
            PendingToolCalls =
            [
                .. _pendingToolCalls.Select(pair => new PendingToolSnapshot(
                    pair.Key.Value, pair.Value.Name, pair.Value.Arguments, positions[pair.Value.Message])),
            ],
            Tokens = TokenStats.Capture(),
            Deliverables =
            [
                .. Deliverables.Presentations.Select(presentation => new DeliverablePresentationSnapshot(
                    presentation.Seq,
                    [.. presentation.Files.Select(file => new PresentedFileSnapshot(file.Path, file.Description))])),
            ],
            Diffs = diffs,
            DisplayTurn = _displayTurn,
            CurrentDisplayTurn = _currentDisplayTurn,
        };
    }

    /**
     * 由缓存恢复投影。调用时 Messages/TraceItems 等必须是清空后的状态(ShowSession 已经清过)。
     *
     * 恢复不触发逐条视图通知: 整段还原完通知一次, 与重放结束时的做法一致。
     */
    internal void RestoreProjection(ProjectionSnapshot snapshot)
    {
        var messages = new MessageViewModel[snapshot.Messages.Count];
        for (var index = 0; index < messages.Length; index++)
            messages[index] = RestoreMessage(snapshot, snapshot.Messages[index]);
        foreach (var message in messages)
        {
            message.Updated += _ => MessagesChanged?.Invoke();
            message.DiffPreviewExpanded += RequestDiffBody;
            Messages.Add(message);
            // 缓存里就是展开态的预览卡片: 不等用户再点一次, 直接把正文补回来。
            if (message.IsDiffExpanded)
                RequestDiffBody(message);
        }
        foreach (var item in snapshot.Trace)
        {
            TraceItems.Add(new TraceItemViewModel
            {
                Seq = item.Seq,
                Kind = (TraceKind)item.Kind,
                Title = item.Title,
                Detail = item.Detail,
                Time = item.Time,
                Message = item.Message is { } index && index < messages.Length ? messages[index] : null,
            });
        }
        RefreshTraceView();
        var bySeq = new Dictionary<long, MessageViewModel>();
        foreach (var message in messages)
        {
            if (message.Seq is { } seq)
                bySeq[seq] = message;
        }
        _lastUserMessages.Clear();
        foreach (var seq in snapshot.LastUserMessageSeqs)
        {
            if (bySeq.TryGetValue(seq, out var message))
                _lastUserMessages.Add(message);
        }
        _pendingToolCalls.Clear();
        foreach (var pending in snapshot.PendingToolCalls)
        {
            if (pending.Message < messages.Length)
                _pendingToolCalls[ToolCallId.Create(pending.CallId)] = (pending.Name, pending.Arguments, messages[pending.Message]);
        }
        _renderedSeq = snapshot.NextSeq;
        _renderedAny = snapshot.RenderedAny;
        _displayTurn = snapshot.DisplayTurn;
        _currentDisplayTurn = snapshot.CurrentDisplayTurn;
        _currentSeq = snapshot.NextSeq - 1;
        IsBusy = snapshot.IsBusy;
        _compactionBusy = snapshot.CompactionBusy;
        // 还原出来的"还在流"的气泡要接回打开游标: 否则后续增量会另起一条新气泡,
        // 界面上就成了同一段回复被劈成两半(缓存恢复之后继续流式必现)。
        _openAssistant = null;
        _openReasoning = null;
        foreach (var message in messages)
        {
            switch (message)
            {
                case { IsStreaming: false }:
                    continue;
                case { Kind: MessageKind.Assistant }:
                    _openAssistant = message;
                    break;
                case { Kind: MessageKind.Reasoning }:
                    _openReasoning = message;
                    break;
            }
        }
        _requestHeader = snapshot.RequestHeader is { } header
            ? new LlmCallConfig(
                header.Provider,
                header.Model,
                header.ReasoningEffort is { Length: > 0 } effort ? ReasoningEffortId.Create(effort) : null,
                header.Temperature,
                header.MaxTokens,
                header.Stop)
            : null;
        TokenStats.Restore(snapshot.Tokens);
        Deliverables.Restore(
            [.. snapshot.Deliverables.Select(presentation => (
                presentation.Seq,
                (IReadOnlyList<PresentedFile>)[.. presentation.Files.Select(file => new PresentedFile(file.Path, file.Description))]))],
            canOpen: true);
        MessagesChanged?.Invoke();
    }

    private static MessageViewModel RestoreMessage(ProjectionSnapshot snapshot, MessageSnapshot item)
    {
        var message = new MessageViewModel(item.Role, item.Text, (MessageKind)item.Kind, item.IsStreaming)
        {
            Seq = item.Seq,
            Time = item.Time,
            ToolName = item.ToolName,
            ArgumentText = item.ArgumentText,
            RenderSeq = item.RenderSeq,
            IsSubagentTool = item.IsSubagentTool,
            SubagentLabel = item.SubagentLabel,
            SubagentSessionId = item.SubagentSessionId is { Length: > 0 } id ? SessionId.Create(id) : null,
        };
        message.Detail = item.Detail;
        message.ResultText = item.ResultText;
        message.HasResult = item.HasResult;
        message.IsToolError = item.IsToolError;
        message.Feedback = (MessageFeedback)item.Feedback;
        if (item.Diff is { } diff && diff < snapshot.Diffs.Count)
        {
            // SetDiff 会把消息展开以露出卡片标题栏, 缓存里的折叠态随后覆盖回去。
            // sourceSeq 非 null 表示快照里只有预览: 展开时按它回头取正文(见 UpgradeCachedDiffAsync)。
            message.SetDiff(snapshot.Diffs[diff].ToCard(), item.DiffResultSeq);
        }
        message.IsExpanded = item.IsExpanded;
        message.IsDiffExpanded = item.IsDiffExpanded;
        return message;
    }

    /** 预览卡片被展开(异步补正文, 不等它)。 */
    private void RequestDiffBody(MessageViewModel message) => _ = UpgradeCachedDiffAsync(message);

    /**
     * 缓存里的 diff 卡片只有预览: 展开时回头取**原始事件**, 重跑一次提取还原正文。
     *
     * 取的是"工具调用 + 工具结果"两条事件, 提取走的是重放路径同一处的 DiffCardExtractor —— 这里
     * 不另写一套 diff 生成, 否则缓存恢复出来的卡片与重放出来的会慢慢长得不一样。
     * 取不回来(远端机器、宿主不支持按区间取、那条事件已被回退)就停在预览, 卡片上标着"仅缓存前 N 行"。
     */
    private async Task UpgradeCachedDiffAsync(MessageViewModel message)
    {
        if (_disposed || message.DiffSourceSeq is not { } resultSeq || message.Seq is not { } callSeq)
            return;
        var session = _chat.Id;
        try
        {
            var events = await _chat.PeekAsync([callSeq, resultSeq]);
            if (_disposed || _chat.Id != session)
                return;
            if (events.GetValueOrDefault(callSeq)?.Data is not ToolCallPayload call
                || events.GetValueOrDefault(resultSeq)?.Data is not ToolResultPayload { Error: null } result)
                return;
            var text = ToolPresentation.FormatResult(SessionText.ContentText(result.Message.Content));
            if (DiffCardExtractor.TryExtract(call.Name, call.Arguments, result.Meta, text) is { } card)
                message.SetDiff(card);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _ctx.LoggerFor("gui").Warn($"diff body refetch #{callSeq}/#{resultSeq} failed: {error.Message}");
        }
    }
}