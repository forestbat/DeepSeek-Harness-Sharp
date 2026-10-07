using Dsh.Tui;

namespace Dsh.Tests;

public class TerminalRawModeTests
{
    [Fact]
    public void LinuxTermiosLayout_MatchesGlibcOffsets()
    {
        if (!OperatingSystem.IsLinux())
            return;

        Assert.Equal(60, TerminalRawMode.LinuxTermiosSizeForTests);
        Assert.Equal(23, TerminalRawMode.LinuxVMinOffsetForTests);
        Assert.Equal(22, TerminalRawMode.LinuxVTimeOffsetForTests);
    }

    [Fact]
    public void LinuxTcGetAttr_IsCallableWithoutWritingTerminal()
    {
        if (!OperatingSystem.IsLinux())
            return;

        _ = TerminalRawMode.TryProbeLinuxTermios();
    }

    /** 鼠标上报序列只由拥有用户终端的进程发出; 常驻会话(ConPTY)不得发出。 */
    [Fact]
    public void Mouse_Reports_Only_Emitted_By_Terminal_Owner()
    {
        Assert.Contains("1006", TerminalRawMode.MouseReportEnableSequenceFor(enableMouse: true, emitMouseReports: true));
        Assert.Equal("", TerminalRawMode.MouseReportEnableSequenceFor(enableMouse: true, emitMouseReports: false));
        Assert.Equal("", TerminalRawMode.MouseReportEnableSequenceFor(enableMouse: false, emitMouseReports: true));
    }
}