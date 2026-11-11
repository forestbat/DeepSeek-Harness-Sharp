using Dsh.Core;
using Dsh.Runtime;
using Dsh.Tools;
using Dsh.Subprocess;
using Dsh.Agent;

namespace Dsh.Tests;

/** 跨平台 shell 的可见性: Windows 上不把 bash 展示给模型, 非 Windows 上只在确实装了 PowerShell Core(6+) 时才展示 pwsh。 */
public sealed class ShellPlatformVisibilityTests
{
    [Fact]
    public void Bash_IsVisibleOnlyOutsideWindows()
    {
        Assert.Equal(!OperatingSystem.IsWindows(), IsVisible(BashTool.ToolName, ctx => BashTool.Register(ctx)));
    }

    [Fact]
    public void Pwsh_IsVisibleOnlyWhenPowerShellCoreIsAvailable()
    {
        Assert.Equal(PowerShellCoreProbe.IsAvailable(), IsVisible(PwshTool.ToolName, ctx => PwshTool.Register(ctx)));
    }

    [Fact]
    public void WindowsPowerShell_IsVisibleOnlyOnWindows()
    {
        Assert.Equal(
            OperatingSystem.IsWindows(),
            IsVisible(WindowsPowerShellTool.ToolName, ctx => WindowsPowerShellTool.Register(ctx)));
    }

    private static bool IsVisible(string toolName, Func<Context, IDisposable> register)
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });
        var tools = new ToolRuntime(ctx);
        _ = new SubprocessService(ctx);
        using var registration = register(ctx);
        return tools.Schemas().Any(schema => schema.Name == toolName);
    }
}
