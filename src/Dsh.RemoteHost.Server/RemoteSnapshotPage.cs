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
            var size = EncodedSize(events[index]);
            if (page.Count > 0 && bytes + size > budget)
                break;
            page.Add(events[index]);
            bytes += size;
        }
        return snapshot with { Events = page };
    }

    /**
     * 一条事件编码后的字节数。
     * 这里只需要"多少字节", 所以写进一个丢弃内容的计数器, 不为每条事件分配一个 byte[]:
     * 12 MB 的页里每条事件都分配一次输出缓冲, 峰值内存和 GC 压力都被放大一倍以上,
     * 而这些字节随即被丢掉 —— 真正要发出去的整页稍后还会再编码一次。
     *
     * 编码器必须取自上下文自己的 Options: 源生成的上下文配了 WhenWritingNull 与 UseStringEnumConverter,
     * 手写一份 JsonWriterOptions 会与实际编码不一致(可空字段被多写出来), 分页就会提前切。
     */
    private static int EncodedSize(SessionEvent sessionEvent)
    {
        var counter = new CountingStream();
        using (var writer = new Utf8JsonWriter(counter, WriterOptions))
            JsonSerializer.Serialize(writer, sessionEvent, HostProtocolJsonContext.Default.SessionEvent);
        return counter.Count;
    }

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        // 与 HostProtocolJsonContext 的输出保持一致: 不转义非 ASCII, 中文/emoji 按 UTF-8 原样计字节。
        Encoder = HostProtocolJsonContext.Default.Options.Encoder,
    };

    /** 只数字节、不保存内容的写入目标。 */
    private sealed class CountingStream : Stream
    {
        public int Count { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }

        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
        public override void Write(byte[] buffer, int offset, int count) => Count += count;
        public override void WriteByte(byte value) => Count += 1;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Count += buffer.Length;
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Count += count;
            return Task.CompletedTask;
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
