using Dsh.Pty;

namespace Dsh.Tui;

/**
 * 常驻 TUI(跑在 daemon pty 里)与 daemon 的协作桥:
 * 上报 agent 会话归属(identify)、节流发布窗格快照(publish-panes)、长轮询接收并应用窗格输入(control-read)。
 * 仅在 DSH_PTY_SESSION_ID 存在(本进程确实跑在 daemon pty 里)时启动; 随 ChatWindow 生命周期注销。
 */
internal sealed class PaneBridge : IDisposable
{
    public const int MaxPublishedLines = 200;
    public static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(1);

    private readonly string _ptyId;
    private readonly ChatWindow _window;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _publishLoop;
    private readonly Task _controlLoop;
    private int _lastHash;

    private PaneBridge(string ptyId, ChatWindow window)
    {
        _ptyId = ptyId;
        _window = window;
        _publishLoop = Task.Run(PublishLoopAsync);
        _controlLoop = Task.Run(ControlLoopAsync);
        _ = IdentifySafeAsync();
    }

    /** 若本进程跑在 daemon pty 里则挂载桥, 否则返回 null。 */
    public static PaneBridge? Mount(ChatWindow window)
    {
        var ptyId = Environment.GetEnvironmentVariable(PtySessionProtocol.SessionVariable);
        return string.IsNullOrWhiteSpace(ptyId) ? null : new PaneBridge(ptyId, window);
    }

    private async Task IdentifySafeAsync()
    {
        try
        {
            await PtyDaemonClient.IdentifyAsync(_ptyId, _window.AgentSessionId, _stop.Token);
        }
        catch (Exception)
        {
            // daemon 未起/已退出: 归属上报失败不影响本进程。
        }
    }

    private async Task PublishLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(PublishInterval, _stop.Token);
                try
                {
                    var panes = await _window.SnapshotPaneSnapshotsAsync(MaxPublishedLines);
                    var hash = Hash(panes);
                    if (hash == _lastHash)
                        continue;
                    await PtyDaemonClient.PublishPanesAsync(_ptyId, _window.AgentSessionId, panes, _stop.Token);
                    _lastHash = hash;
                }
                catch (Exception)
                {
                    // 单次发布失败忽略, 下一拍重试。
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ControlLoopAsync()
    {
        long since = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var messages = await PtyDaemonClient.ControlReadAsync(_ptyId, since, _stop.Token);
                foreach (var message in messages)
                {
                    since = Math.Max(since, message.Seq);
                    try
                    {
                        var kind = message.Kind;
                        var paneId = message.PaneId;
                        var payload = message.Payload;
                        await _window.DispatchAsync(() =>
                        {
                            _window.ApplyPaneInput(kind, paneId, payload);
                            return true;
                        });
                    }
                    catch (Exception)
                    {
                        // 目标窗格不存在/不接受输入: 忽略该条, 不打断循环。
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // daemon 瞬断: 桥结束, 不影响 TUI 本体。
        }
    }

    private static int Hash(IReadOnlyList<PtyPaneSnapshotDto> panes)
    {
        var hash = new HashCode();
        foreach (var pane in panes)
        {
            hash.Add(pane.Id);
            hash.Add(pane.Kind);
            hash.Add(pane.Title);
            hash.Add(pane.Focused);
            foreach (var line in pane.Lines ?? [])
                hash.Add(line);
        }
        return hash.ToHashCode();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            Task.WaitAll([_publishLoop, _controlLoop], TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
        _stop.Dispose();
    }
}
