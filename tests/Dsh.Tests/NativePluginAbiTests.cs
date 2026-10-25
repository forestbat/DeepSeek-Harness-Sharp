using System.Runtime.CompilerServices;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Plugins.Native.Host;
using Dsh.Runtime;

namespace Dsh.Tests;

/**
 * 原生 ABI 端到端回归: 宿主装载 NativeAOT 发布的 Dsh.NativePluginSample, 经真实的生成胶水调用工具。
 * 样例需先发布: dotnet publish tests/Dsh.NativePluginSample -c Release; 未发布或非 Linux 时跳过。
 */
public sealed class NativePluginAbiTests
{
    private const string EchoTool = "native_echo";
    private const string LengthTool = "native_length";
    private const string EchoLogMarker = "echo invoked";
    private const int LargeMessageLength = 200_000;

    [Fact]
    public async Task PublishedSample_RunsToolOnce_AndTreatsPluginExceptionsAsToolErrors()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        var load = NativePluginLibrary.TryLoad(PublishedSamplePath());
        Assert.True(load.Succeeded, load.Reason);
        using var registration = (IDisposable)(await NativePluginBridge.CreateDefinition(load.Plugin!).Apply(ctx, null, TestContext.Current.CancellationToken))!;

        var message = $"大消息-{new string('x', LargeMessageLength)}";
        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = EchoTool,
            Arguments = JsonSerializer.SerializeToElement(new { message }),
            Agent = null,
            Signal = default,
        });

        Assert.False(result.IsError);
        Assert.Equal($"echo:{message}", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Equal(1, ctx.Root.Logger.Buffer.Count(line => line.Text.Contains(EchoLogMarker)));
        Assert.Equal(NativePluginState.Active, load.Plugin!.State);
        Assert.Throws<NullReferenceException>(() => LengthOf(null));

        var failed = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = LengthTool,
            Arguments = JsonSerializer.SerializeToElement(new { }),
            Agent = null,
            Signal = default,
        });

        Assert.True(failed.IsError);
        Assert.Equal(NativePluginState.Active, load.Plugin.State);
        Assert.Contains(ctx.Root.Logger.Buffer, line => line.Text.Contains("invoke_tool failed"));
        Assert.NotNull(tools.Get(EchoTool));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int LengthOf(string? text) => text!.Length;

    private static string PublishedSamplePath()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("样例只发布 linux-x64 原生库");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeepSeek-Harness-Sharp.slnx")))
            directory = directory.Parent;
        var path = Path.Combine(directory?.FullName ?? AppContext.BaseDirectory,
            "tests", "Dsh.NativePluginSample", "bin", "Release", "net10.0", "linux-x64", "publish", "Dsh.NativePluginSample.so");
        if (!File.Exists(path))
            Assert.Skip($"未发布原生样例({path}), 先运行 dotnet publish tests/Dsh.NativePluginSample -c Release");
        return path;
    }
}
