using Dsh.Tui;
using Xunit;

namespace Dsh.Tests;

/** `--gpu-card` 取值: 纯卡号补成卡节点路径, 路径(含 by-path 符号链接)原样使用。 */
public class GpuCardSelectionTests
{
    [Theory]
    [InlineData("1", "/dev/dri/card1")]
    [InlineData("0", "/dev/dri/card0")]
    [InlineData("12", "/dev/dri/card12")]
    [InlineData("/dev/dri/card2", "/dev/dri/card2")]
    [InlineData("/dev/dri/by-path/pci-0000:11:00.0-card", "/dev/dri/by-path/pci-0000:11:00.0-card")]
    public void NormalizeCardPath_Accepts_Index_Or_Path(string value, string expected)
        => Assert.Equal(expected, EglGbmKmsHost.NormalizeCardPath(value));
}
