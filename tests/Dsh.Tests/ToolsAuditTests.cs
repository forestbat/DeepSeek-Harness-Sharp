using System.Text.Json;
using Dsh.Agent;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tools;

namespace Dsh.Tests;

public sealed class ToolsAuditTests
{
    [Theory]
    [InlineData(true, "*.txt", true)]
    [InlineData(true, "input.txt", true)]
    [InlineData(true, "*.{txt,md}", true)]
    [InlineData(true, "*.cs", false)]
    [InlineData(false, "*.txt", true)]
    [InlineData(false, "*.cs", false)]
    public async Task Grep_IncludeFiltersFileNamesForExplicitFilesAndDirectories(bool explicitFile, string include, bool expectedMatch)
    {
        var directory = TempTree.CreateDirectory("grep-file-filter");
        try
        {
            var file = Path.Combine(directory, "input.txt");
            await File.WriteAllTextAsync(file, "first line\nneedle on second line\n", TestContext.Current.CancellationToken);
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var tools = new ToolRuntime(ctx);
            using var registration = GrepTool.Register(ctx);
            var result = await tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create("grep-file-filter"),
                Name = GrepTool.ToolName,
                Arguments = JsonSerializer.SerializeToElement(new { pattern = "needle", path = explicitFile ? file : directory, include }, DshJson.Options),
                Signal = TestContext.Current.CancellationToken,
            });

            var success = Assert.IsType<ToolExecutionResult.Success>(result);
            var matches = success.Value.GetProperty("matches").EnumerateArray().ToArray();
            if (!expectedMatch)
            {
                Assert.Empty(matches);
                return;
            }
            var match = Assert.Single(matches);
            Assert.Equal(file, Path.GetFullPath(match.GetProperty("path").GetString()!, Environment.CurrentDirectory));
            Assert.Equal(2, match.GetProperty("lineNumber").GetInt32());
            Assert.Equal("needle on second line", match.GetProperty("line").GetString());
        }
        finally
        {
            TempTree.Delete(directory);
        }
    }
}
