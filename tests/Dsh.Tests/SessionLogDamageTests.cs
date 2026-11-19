using System.Text;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 损伤日志的容错: 一条记录读不出来不能让整份会话打不开, 也不能把磁盘上的原始字节删掉。
 * 这两条是会话历史事故(2026-11-17)的直接教训。
 */
public class SessionLogDamageTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-damage-proj";
    private readonly List<string> _roots = [];

    private string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-damage-{Guid.NewGuid():N}");
        _roots.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static SessionHeader NewHeader()
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        return new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 1700000000000,
            Cwd = TestCwd,
            IsSeeded = false,
        };
    }

    private static List<SessionEvent> BuildEvents(int count)
        =>
        [
            .. Enumerable.Range(0, count).Select(index => new SessionEvent
            {
                Type = SessionEventTypes.StepStart,
                Seq = index,
                Time = 1700000000000 + index,
                Data = new StepStartPayload(1, index),
            })
        ];

    /**
     * 建一份日志并返回日志路径。
     * 损伤用例要能挑出"某一帧"来改坏, 所以这里显式要求"一条事件一帧": 把批次上限设成 1,
     * 而不是依赖写路径自己碰巧每次都落盘。
     */
    private string WriteLog(string root, SessionHeader header, JsonlCompression compression, int eventCount)
    {
        using var persistence = new JsonlSessionPersistence(root, compression: compression,
            writeBatch: new JsonlWriteBatch(MaxEvents: 1, Interval: TimeSpan.FromHours(1)));
        var handle = persistence.Create(header);
        foreach (var sessionEvent in BuildEvents(eventCount))
            handle.Append([sessionEvent]);
        handle.Close();
        return JsonlLayout.LogPath(root, header.Cwd, header.Id, compression);
    }

    [Theory]
    [InlineData(JsonlCompression.None)]
    [InlineData(JsonlCompression.Zstd)]
    public void UnreadableRecord_DoesNotBlockOpeningAndKeepsTheFile(JsonlCompression compression)
    {
        var root = NewRoot();
        var header = NewHeader();
        var logPath = WriteLog(root, header, compression, eventCount: 6);


        // 追加一条解析不出的记录: 它就是"新版本写的、本 harness 不认"的那类内容。
        using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write))
        {
            var garbage = "{\"type\":\"future/thing\",\"seq\":6,\"time\":1,\"data\":{\"x\":1}}\n";
            var bytes = Encoding.UTF8.GetBytes(garbage);
            stream.Write(compression == JsonlCompression.Zstd ? ZstdFrames.CompressFrame(bytes) : bytes);
        }

        var before = new FileInfo(logPath).Length;
        var warned = new List<string>();
        using var persistence = new JsonlSessionPersistence(root, compression: compression, warn: warned.Add);
        var reader = persistence.Open(header.Id, SessionAccess.Read);
        var events = reader.Read().ToArray();
        reader.Close();

        Assert.Equal(7, events.Length);
        // 未知类型按不透明记录保留: 序号不重排, 后面的事件仍然对得上自己的 seq。
        Assert.Equal("future/thing", events[^1].Type);
        Assert.Equal(6, events[^1].Seq);
        Assert.Equal(before, new FileInfo(logPath).Length);
    }

    [Theory]
    [InlineData(JsonlCompression.None)]
    [InlineData(JsonlCompression.Zstd)]
    public void WriteOpen_PreservesOriginalBytesAndRewritesTheReadablePrefix(JsonlCompression compression)
    {
        var root = NewRoot();
        var header = NewHeader();
        var logPath = WriteLog(root, header, compression, eventCount: 5);

        using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write))
        {
            var garbage = "not-a-session-event-at-all\n";
            var bytes = Encoding.UTF8.GetBytes(garbage);
            stream.Write(compression == JsonlCompression.Zstd ? ZstdFrames.CompressFrame(bytes) : bytes);
        }

        var damagedLength = new FileInfo(logPath).Length;

        var warned = new List<string>();
        using var persistence = new JsonlSessionPersistence(root, compression: compression, warn: warned.Add);
        var writer = persistence.Open(header.Id, SessionAccess.Write);
        var appended = new SessionEvent
        {
            Type = SessionEventTypes.StepStart, Seq = 5, Time = 1700000001000, Data = new StepStartPayload(1, 99),
        };
        writer.Append([appended]);
        writer.Close();

        // 原文一个字节都没丢: 就在旁边的 .damaged-<时间> 里。
        var sidecars = Directory.GetFiles(Path.GetDirectoryName(logPath)!, "*.damaged-*");
        var sidecar = Assert.Single(sidecars);
        Assert.Equal(damagedLength, new FileInfo(sidecar).Length);
        Assert.Contains(warned, message => message.Contains("preserved", StringComparison.Ordinal));

        var reopened = persistence.Open(header.Id, SessionAccess.Read);
        var events = reopened.Read().ToArray();
        reopened.Close();
        Assert.Equal(6, events.Length);
        Assert.Equal(99L, ((StepStartPayload)events[^1].Data).Step);
        for (var index = 0; index < events.Length; index += 1)
            Assert.Equal(index, events[index].Seq);
    }

    [Fact]
    public void TrailingPartialFrame_IsTruncatedWithoutSidecar()
    {
        var root = NewRoot();
        var header = NewHeader();
        var logPath = WriteLog(root, header, compression: JsonlCompression.Zstd, eventCount: 4);

        var tornFrame =
            ZstdFrames.CompressFrame(
                Encoding.UTF8.GetBytes("{\"type\":\"step/start\",\"seq\":4,\"time\":1,\"data\":{}}\n"));
        using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write))
            stream.Write(tornFrame.AsSpan(0, tornFrame.Length / 2));

        var warned = new List<string>();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.Zstd, warn: warned.Add);
        var writer = persistence.Open(header.Id, SessionAccess.Write);
        var appended = new SessionEvent
        {
            Type = SessionEventTypes.StepStart, Seq = 4, Time = 1700000002000, Data = new StepStartPayload(1, 7),
        };
        writer.Append([appended]);
        writer.Close();

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(logPath)!, "*.damaged-*"));
        Assert.Empty(warned);
        var reopened = persistence.Open(header.Id, SessionAccess.Read);
        Assert.Equal(5, reopened.Read().ToArray().Length);
        reopened.Close();
    }

    [Fact]
    public void CorruptFrameInTheMiddle_KeepsTheLaterFrames()
    {
        var root = NewRoot();
        var header = NewHeader();
        var logPath = WriteLog(root, header, compression: JsonlCompression.Zstd, eventCount: 6);

        // 把中间一帧的内容字节改坏(保留帧头), 模拟跨版本编解码漂移。
        var bytes = File.ReadAllBytes(logPath);
        var scanned = ZstdFrames.Scan(bytes);
        Assert.True(scanned.Frames.Count >= 4);
        var victim = scanned.Frames[1];
        for (var index = victim.Start + 6; index < victim.End - 1; index += 3)
            bytes[index] ^= 0xFF;
        File.WriteAllBytes(logPath, bytes);

        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.Zstd);
        var reader = persistence.Open(header.Id, SessionAccess.Read);
        var events = reader.Read().ToArray();
        reader.Close();

        Assert.NotEmpty(events);
        Assert.True(events.Length < 6, "最少丢掉被改坏那一帧里的事件");
        for (var index = 0; index < events.Length; index += 1)
            Assert.Equal(index, events[index].Seq);
    }

    /**
     * 未知类型不再被丢弃, 而是按不透明记录保留 —— 这样序号不必重排,
     * sourceEventSeqs / surfaceOp.start 这类"指向别的序号"的字段才不会错位。
     */
    [Fact]
    public void UnknownEventType_IsPreservedAsOpaqueInsteadOfRejected()
    {
        var root = NewRoot();
        var header = NewHeader();
        var logPath = WriteLog(root, header, compression: JsonlCompression.None, eventCount: 4);

        var lines = File.ReadAllLines(logPath).ToList();
        lines.Insert(2, "{\"type\":\"brand/new-event\",\"seq\":99,\"time\":5,\"data\":{\"k\":1}}");
        File.WriteAllLines(logPath, lines);

        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
        var reader = persistence.Open(header.Id, SessionAccess.Read);
        var events = reader.Read().ToArray();
        reader.Close();

        Assert.Equal(5, events.Length);
        Assert.Contains(events, item => item.Type == "brand/new-event");
        Assert.IsType<UnknownSessionEventPayload>(events.Single(item => item.Type == "brand/new-event").Data);
    }
}
