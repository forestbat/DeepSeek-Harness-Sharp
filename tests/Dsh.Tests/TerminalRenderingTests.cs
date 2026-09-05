using System.Text;
using Dsh.Terminal;

namespace Dsh.Tests;

public class TerminalRenderingTests
{
    [Fact]
    public void RendersSpawnWithAndWithoutNamesOrMotd()
    {
        var empty = TerminalRendering.RenderSpawn(
            new TerminalSpawnResult(new TerminalSessionId("pty-1"), null, "shell", null, TerminalSessionStatus.Running(), ""),
            1024);
        Assert.Equal("started terminal session pty-1 [type: shell]\n(no startup output)", empty);

        var named = TerminalRendering.RenderSpawn(
            new TerminalSpawnResult(new TerminalSessionId("pty-2"), "main", "shell", 2, TerminalSessionStatus.Running(), "ready"),
            1024);
        Assert.Contains("pty-2 (main)", named);
    }

    [Fact]
    public void RendersRunningExitedEmptyAndTruncatedSends()
    {
        var empty = TerminalRendering.RenderSend(
            new TerminalSendResult("", TerminalWaitReason.Timeout, TerminalSessionStatus.Running(), true),
            1024);
        Assert.Equal("(no new output)\n[wait: timeout]\n[session: running]\n[output truncated]", empty);

        var exited = TerminalRendering.RenderSend(
            new TerminalSendResult("bye", TerminalWaitReason.SessionExit, TerminalSessionStatus.Exited(null, "SIGTERM"), false),
            1024);
        Assert.Contains("exited code=null signal=SIGTERM", exited);

        Assert.Equal("[output truncated]", TerminalRendering.RenderSendRead(new TerminalSendRead("", true)));
        Assert.Equal("x\n[output truncated]", TerminalRendering.RenderSendRead(new TerminalSendRead("x", true)));
        Assert.Equal("x\n[output truncated]", TerminalRendering.RenderSendRead(new TerminalSendRead("x\n", true)));
        Assert.Equal("x", TerminalRendering.RenderSendRead(new TerminalSendRead("x", false)));
    }

    [Fact]
    public void RendersHistoryAndEveryListStatusShape()
    {
        var read = TerminalRendering.RenderRead(
            new TerminalReadResult("", 0, 0, 0, true),
            1024);
        Assert.Equal("(no retained output)\n[lines: 0-0 of 0]\n[output truncated]", read);

        Assert.Equal("(no terminal sessions)", TerminalRendering.RenderList([], 1024));

        var list = TerminalRendering.RenderList(
        [
            new TerminalSessionSnapshot(new TerminalSessionId("pty-1"), null, "shell", null, TerminalSessionStatus.Running()),
            new TerminalSessionSnapshot(new TerminalSessionId("pty-2"), "done", "shell", 9, TerminalSessionStatus.Exited(2, null)),
            new TerminalSessionSnapshot(new TerminalSessionId("pty-3"), null, "shell", null, TerminalSessionStatus.Exited(null, "SIGTERM")),
            new TerminalSessionSnapshot(new TerminalSessionId("pty-4"), null, "shell", null, TerminalSessionStatus.Exited(null, null)),
        ], 1024);
        Assert.Equal(
            "pty-1 [shell] running\npty-2 (done) [shell] exited code=2 signal=null pid=9\npty-3 [shell] exited code=null signal=SIGTERM\npty-4 [shell] exited code=null signal=null",
            list);
    }

    [Fact]
    public void BoundsCompleteUtf8ResultsWhileRetainingMetadataWhenItFits()
    {
        var send = TerminalRendering.RenderSend(
            new TerminalSendResult($"prefix-{new string('界', 40)}", TerminalWaitReason.StdinRead, TerminalSessionStatus.Running(), false),
            64);
        Assert.True(Encoding.UTF8.GetByteCount(send) <= 64);
        Assert.Contains("[wait: stdin_read]", send);
        Assert.Contains("[output truncated]", send);

        var read = TerminalRendering.RenderRead(
            new TerminalReadResult(new string('x', 200), 20, 0, 10, false),
            48);
        Assert.True(Encoding.UTF8.GetByteCount(read) <= 48);
        Assert.Contains("[lines: 0-10 of 20]", read);

        var bounded = TerminalRendering.BoundTerminalText(new string('x', 200), 32);
        Assert.True(Encoding.UTF8.GetByteCount(bounded) <= 32);
        Assert.EndsWith("[output truncated]", bounded);
    }
}
