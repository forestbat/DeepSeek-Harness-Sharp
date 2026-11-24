using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 尾巴上出现"不属于任何事件"的字节时, 会话必须**当场**修好继续写。
 *
 * 宿主可能连续开着几天: 把这种情形判成终局错误等于这期间一条都不落盘。
 * 修的时候只截掉无法归属的部分, 读得出来的事件全留, 原字节另存一份供取证。
 */
public sealed class SessionDirtyTailRepairTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-dirty-tail";
    private const int ForeignTailBytes = 85;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dsh-dirty-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ForeignBytesAtTheTail_AreCutOffAndWritingContinues()
    {
        using var persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        var header = NewHeader();
        using var handle = persistence.Create(header);
        var session = Session.Create(header.Id, header: header);
        handle.TailSource = from => [.. session.SnapshotEvents(from)];
        handle.RebaseSource = session.RebaseUnsaved;
        session.AttachSequencer(handle.Sequencer);

        handle.Append([session.Append(new TurnStartPayload(1))]);
        handle.Flush();
        var logPath = JsonlLayout.LogPath(_root, header.Cwd, header.Id, JsonlCompression.None);
        var cleanLength = new FileInfo(logPath).Length;

        // 冒充"另一个写入方往同一份日志尾部塞了无法归属的字节"。
        using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write))
            stream.Write(new byte[ForeignTailBytes]);

        handle.Append([session.Append(new StepStartPayload(1, 1))]);
        handle.Flush();

        // 事件照常落盘, 序号连续; 外来字节被截掉, 原文件另存一份。
        using var reader = persistence.Open(header.Id, SessionAccess.Read);
        Assert.Equal([0L, 1L], reader.Read().Select(sessionEvent => sessionEvent.Seq));
        var sidecar = Directory.GetFiles(Path.GetDirectoryName(logPath)!, $"{Path.GetFileName(logPath)}.damaged-*").Single();
        Assert.True(new FileInfo(sidecar).Length >= cleanLength + ForeignTailBytes);
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
            IsSeeded = false
        };
    }
}
