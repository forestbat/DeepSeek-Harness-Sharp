using Dsh.Terminal;

namespace Dsh.Tests;

public class TerminalBashConfigTests
{
    [Fact]
    public void ResolvesBashDefaults()
    {
        var resolved = TerminalBashConfigResolver.Resolve(new TerminalBashConfig
        {
            BackendType = "shell",
            Rows = 24,
            Cols = 80,
        });
        Assert.Equal(ShellDialect.Bash, resolved.ShellDialect);
        Assert.Equal(OperatingSystem.IsWindows() ? "bash" : "/bin/bash", resolved.ShellPath);
        Assert.Equal(new[] { "--noprofile", "--norc", "-i" }, resolved.ShellArgs);
    }

    [Fact]
    public void ResolvesPwshDefaultsAndValidates()
    {
        var resolved = TerminalBashConfigResolver.Resolve(new TerminalBashConfig
        {
            BackendType = "shell",
            ShellDialect = ShellDialect.Pwsh,
            Rows = 24,
            Cols = 80,
        });
        Assert.Equal(ShellDialect.Pwsh, resolved.ShellDialect);
        Assert.NotEmpty(resolved.ShellPath);
        Assert.Equal(new[] { "-NoLogo", "-NoProfile" }, resolved.ShellArgs);
        TerminalBashConfigResolver.Validate(resolved);
    }

    [Fact]
    public void LetsExplicitShellSpecificationWin()
    {
        var resolved = TerminalBashConfigResolver.Resolve(new TerminalBashConfig
        {
            BackendType = "shell",
            ShellDialect = ShellDialect.Pwsh,
            ShellPath = "/custom/pwsh",
            ShellArgs = ["-NoProfile"],
            Rows = 24,
            Cols = 80,
        });
        Assert.Equal("/custom/pwsh", resolved.ShellPath);
        Assert.Equal(new[] { "-NoProfile" }, resolved.ShellArgs);
    }

    [Fact]
    public void TreatsEmptyShellValuesAsUnset()
    {
        var resolved = TerminalBashConfigResolver.Resolve(new TerminalBashConfig
        {
            BackendType = "shell",
            ShellDialect = ShellDialect.Bash,
            ShellPath = "",
            ShellArgs = [],
            Rows = 24,
            Cols = 80,
        });
        Assert.Equal(OperatingSystem.IsWindows() ? "bash" : "/bin/bash", resolved.ShellPath);
        Assert.Equal(new[] { "--noprofile", "--norc", "-i" }, resolved.ShellArgs);
    }

    [Fact]
    public void RejectsEmptyNamesInvalidNumbersAndReadCapAboveRetention()
    {
        Assert.Throws<InvalidOperationException>(() => TerminalBashConfigResolver.Validate(TerminalBashConfigResolver.Resolve(new TerminalBashConfig { BackendType = "" })));
        Assert.Throws<InvalidOperationException>(() => TerminalBashConfigResolver.Validate(TerminalBashConfigResolver.Resolve(new TerminalBashConfig { Rows = 0 })));
        Assert.Throws<InvalidOperationException>(() => TerminalBashConfigResolver.Validate(TerminalBashConfigResolver.Resolve(new TerminalBashConfig { MaxReadBytes = 2048, ScrollbackMaxBytes = 1024 })));
    }

    [Fact]
    public void RejectsHandoffGraceShorterThanOnePoll()
    {
        Assert.Throws<InvalidOperationException>(() => TerminalBashConfigResolver.Validate(TerminalBashConfigResolver.Resolve(new TerminalBashConfig
        {
            HandoffGraceMs = 9,
            PollIntervalMs = 10,
        })));
        TerminalBashConfigResolver.Validate(TerminalBashConfigResolver.Resolve(new TerminalBashConfig
        {
            HandoffGraceMs = 10,
            PollIntervalMs = 10,
        }));
    }
}
