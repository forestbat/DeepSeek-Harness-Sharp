using System.Text.Json;
using Dsh.Core;

namespace Dsh.RemoteHost;

/**
 * 会话历史按帧上限切页: 历史整段塞进一帧会超过 FrameCodec 的上限, 而一帧写不出去会把整条连接带走。
 * 所以一页受字节预算约束, 客户端凭 TotalEventCount 继续取下一页。
 */
internal static class RemoteSnapshotPage
{
    /** 切一页历史: 从 fromEvent 起逐条累加到预算用尽; 单条就超预算时也发出, 否则这条事件永远取不到。 */
    public static RemoteSessionSnapshot Slice(RemoteSessionSnapshot snapshot, long fromEvent, int maxBytes)
    {
        var events = snapshot.Events;
        var start = (int)Math.Clamp(fromEvent, 0L, events.Count);
        var budget = Math.Clamp(maxBytes, 1, HostProtocol.SnapshotPageBytes);
        var page = new List<SessionEvent>();
        var bytes = 0;
        for (var index = start; index < events.Count; index += 1)
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(events[index], HostProtocolJsonContext.Default.SessionEvent).Length;
            if (page.Count > 0 && bytes + size > budget)
                break;
            page.Add(events[index]);
            bytes += size;
        }
        return snapshot with { Events = page };
    }
}
