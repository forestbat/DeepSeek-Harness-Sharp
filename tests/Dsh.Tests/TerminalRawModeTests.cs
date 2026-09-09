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
}