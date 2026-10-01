using Dsh.Pty;

namespace Dsh.Tests;

/** `/detach` 的默认命令来自 PtyShell: 必须按平台解析(Windows 取 pwsh/powershell/cmd, Unix 取 $SHELL 或 bash/sh)。 */
public sealed class PtyShellTests
{
    [Fact]
    public void Resolve_Returns_A_Platform_Shell()
    {
        var shell = PtyShell.Resolve();

        Assert.False(string.IsNullOrWhiteSpace(shell));
        if (OperatingSystem.IsWindows())
        {
            // Windows 上没有 /bin/sh: 以前 /detach 的硬编码默认值必然起不来。
            Assert.DoesNotContain("/bin/", shell, StringComparison.Ordinal);
            Assert.EndsWith(".exe", shell, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.StartsWith("/", shell, StringComparison.Ordinal);
            Assert.True(File.Exists(shell), shell);
        }

        Assert.NotNull(PtyShell.Arguments(shell));
    }
}
