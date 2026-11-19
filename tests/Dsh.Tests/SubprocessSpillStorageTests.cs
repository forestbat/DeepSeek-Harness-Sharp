using System.Text.Json;
using Dsh.Agent;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Subprocess;
using Dsh.Tools;

namespace Dsh.Tests;

/**
 * 命令输出超出上限时, 原文必须落在 storage 配置的根下(spill 分区), 不再写系统临时目录;
 * 返回给模型的截断提示里给出的路径就是那个真实文件。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class SubprocessSpillStorageTests
{
    private const string TruncationMarker = "[output truncated; full output: ";

    [Fact(Timeout = 120000)]
    public async Task TruncatedCommandOutput_SpillsUnderStorageRoot()
    {
        if (!ShellProbe.Pwsh)
            Assert.Skip("pwsh 不可用, 跳过依赖外部 shell 的用例");
        var workspace = Path.Combine(Path.GetTempPath(), $"dsh-spill-storage-{Guid.NewGuid():N}");
        var storageRoot = Path.Combine(workspace, "storage");
        try
        {
            var ctx = new Context();
            ctx.SetOwn(DshPaths.PropName, DshPaths.Default(storageRoot));
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var tools = new ToolRuntime(ctx);
            using var subprocess = new SubprocessService(ctx);
            PwshTool.Register(ctx, new BashToolConfig { MaxOutputBytes = 64, MaxSpillBytes = 1024 * 1024 });

            var result = await Execute(tools, """{"command":"1..50 | ForEach-Object { 'line-' + $_ }"}""");

            Assert.False(result.IsError);
            var modelText = Normalize(string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
            var spilled = SpilledPath(modelText);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(storageRoot, "spill")),
                Path.GetFullPath(Path.GetDirectoryName(spilled)!));
            Assert.True(File.Exists(spilled), $"截断提示里的路径打不开: {spilled}");

            var full = Normalize(await File.ReadAllTextAsync(spilled, TestContext.Current.CancellationToken));
            Assert.StartsWith("line-1\n", full);
            Assert.EndsWith("line-50\n", full);
            Assert.DoesNotContain("line-1\n", modelText);
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static Task<ToolExecutionResult> Execute(ToolRuntime tools, string arguments)
        => tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = PwshTool.ToolName,
            Arguments = JsonElement.Parse(arguments),
            Signal = TestContext.Current.CancellationToken,
        });

    /** 从截断提示里取出真实落盘路径。 */
    private static string SpilledPath(string text)
    {
        var start = text.IndexOf(TruncationMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"截断提示缺少完整输出路径: {text}");
        var begin = start + TruncationMarker.Length;
        var end = text.IndexOf(']', begin);
        Assert.True(end > begin, $"截断提示里的路径不完整: {text}");
        return text[begin..end];
    }

    private static string Normalize(string text) => text.ReplaceLineEndings("\n");
}