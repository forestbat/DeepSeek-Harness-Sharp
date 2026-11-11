using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tools;
using Dsh.Subprocess;
using Dsh.Agent;

namespace Dsh.Tests;

/**
 * 停止按钮的真实失效路径: 工具进程里挂着继承了 stdout 的孙进程时,
 * 工具调用必须仍能在根进程退出后返回, 且取消能立刻打断它。
 */
[Collection(SerialProcessCollection.Name)]
public class SubprocessDrainReproTests
{
    [Fact(Timeout = 60000)]
    public async Task RootExit_WithDetachedGrandchildHoldingStdout_Completes()
    {
        var ctx = new Context();
        using var subprocess = new SubprocessService(ctx);
        using var abort = new CancellationTokenSource();
        using var handle = await subprocess.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = DetachedGrandchildCommand(),
            Cwd = Path.GetTempPath(),
            Signal = abort.Token,
        });

        var done = await Task.WhenAny(handle.Done, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(ReferenceEquals(done, handle.Done),
            $"根进程退出后 Done 仍未完成: 孙进程继承了 stdout, 排空永远等不到 EOF (pid {handle.Pid})");
    }

    [Fact(Timeout = 60000)]
    public async Task Cancel_UnblocksEvenWhenGrandchildHoldsStdout()
    {
        var ctx = new Context();
        using var subprocess = new SubprocessService(ctx);
        using var abort = new CancellationTokenSource();
        using var handle = await subprocess.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = DetachedGrandchildCommand(),
            Cwd = Path.GetTempPath(),
            Signal = abort.Token,
        });
        await Task.Delay(500);

        abort.Cancel();
        var done = await Task.WhenAny(handle.Done, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(ReferenceEquals(done, handle.Done),
            $"取消之后 Done 仍未完成: 停止按钮点多少次都不会有反应 (pid {handle.Pid})");
    }

    [Fact(Timeout = 120000)]
    public void ShellTool_ReturnsWhenGrandchildKeepsStdoutOpen()
    {
        if (!ShellProbe.Pwsh)
            Assert.Skip("pwsh 不可用, 跳过依赖外部 shell 的用例");
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        using var subprocess = new SubprocessService(ctx);
        PwshTool.Register(ctx);

        var result = tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = "pwsh",
            Arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                command = "Start-Process -NoNewWindow -FilePath 'ping' -ArgumentList '-n','60','127.0.0.1' | Out-Null; Write-Output started",
                description = "grandchild keeps stdout open",
            }, DshJson.Options)).RootElement,
            Signal = default,
        });

        var finished = result.Wait(TimeSpan.FromSeconds(30));
        Assert.True(finished,
            "工具调用被孙进程占着的管道拖住, 永远不返回: 这就是停止按钮没反应的原因");
        Assert.False(result.Result.IsError);
    }

    /** 启动一个后台且与父进程脱离的孙进程, 它继承父进程的 stdout/stderr 管道。 */
    private static IReadOnlyList<string> DetachedGrandchildCommand()
        => OperatingSystem.IsWindows()
            ? ["cmd.exe", "/c", "start /b ping -n 60 127.0.0.1"]
            : ["/bin/sh", "-c", "setsid sleep 60 & exit 0"];
}
