using System.Text.Json;
using Dsh.Agent;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tools;

namespace Dsh.Tests;

public sealed class ToolsAuditTests
{
    [Theory]
    [InlineData(true, "*.txt", true, "input.txt")]
    [InlineData(true, "input.txt", true, "input.txt")]
    [InlineData(true, "*.{txt,md}", true, "input.txt")]
    [InlineData(true, "*.cs", false, "input.txt")]
    [InlineData(false, "*.txt", true, "input.txt")]
    [InlineData(false, "*.cs", false, "input.txt")]
    [InlineData(true, "MixedName.Txt", true, "MixedName.Txt")]
    [InlineData(true, "*.Txt", true, "MixedName.Txt")]
    [InlineData(true, "*.txt", false, "MixedName.Txt")]
    [InlineData(true, "mixedname.Txt", false, "MixedName.Txt")]
    [InlineData(false, "MixedName.Txt", true, "MixedName.Txt")]
    [InlineData(false, "*.Txt", true, "MixedName.Txt")]
    [InlineData(false, "*.txt", false, "MixedName.Txt")]
    [InlineData(false, "mixedname.Txt", false, "MixedName.Txt")]
    public async Task Grep_IncludeFiltersFileNamesForExplicitFilesAndDirectories(bool explicitFile, string include, bool expectedMatch, string fileName)
    {
        var directory = TempTree.CreateDirectory("grep-file-filter");
        try
        {
            var file = Path.Combine(directory, fileName);
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
            Assert.True(CanonicalFilePath.SameFile(file, Path.GetFullPath(match.GetProperty("path").GetString()!, Environment.CurrentDirectory)));
            Assert.Equal(2, match.GetProperty("lineNumber").GetInt32());
            Assert.Equal("needle on second line", match.GetProperty("line").GetString());
        }
        finally
        {
            TempTree.Delete(directory);
        }
    }
}
