using System.Text.Json;
using Dsh.CSharpRepl;
using Dsh.Core;
using Dsh.Csx;
using Dsh.Llm;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class ExternalFrontendSettingsTests
{
    [Theory(Timeout = 120000)]
    [Trait("Category", "SandboxNative")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledFrontend_CannotWriteSettingsButPreservesRealOutput(bool repl)
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("The opaque C# frontend filesystem boundary requires the Linux backend.");
        using var fixture = new ExternalProcessBoundaryTests.Fixture(sandboxEnabled: true);
        _ = new SystemPrompt(fixture.Context, new SystemPromptConfig());
        var tools = new ToolRuntime(fixture.Context);
        var command = repl ? "csharprepl" : "dotnet-script";
        string executable;
        try
        {
            executable = await fixture.Processes.ResolveExecutable(command, signal: TestContext.Current.CancellationToken);
        }
        catch (FileNotFoundException)
        {
            Assert.Skip("The optional real C# frontend is not installed.");
            return;
        }
        var program = $$"""
            bool denied = false;
            try { System.IO.File.WriteAllText({{JsonSerializer.Serialize(fixture.SettingsPath)}}, "bad"); }
            catch (System.UnauthorizedAccessException) { denied = true; }
            catch (System.IO.IOException) { denied = true; }
            if (!denied) throw new System.Exception("Protected write unexpectedly succeeded.");
            System.IO.File.WriteAllText("frontend-output", "workspace 中文");
            System.Console.WriteLine("denied 中文");
            System.Console.Error.WriteLine("frontend-stderr");
            """;
        using var registration = repl
            ? ToolRunCSharpRepl.Register(fixture.Context, new CSharpReplConfig { Executable = executable })
            : ToolRunCsx.Register(fixture.Context, fixture.Root, new CsxPluginConfig { Executable = executable });
        var script = Path.Combine(fixture.Root, "probe.csx");
        if (!repl)
            await File.WriteAllTextAsync(script, program, TestContext.Current.CancellationToken);
        var arguments = repl
            ? JsonSerializer.SerializeToElement(new { code = program, description = "Exercise protected BCL IO", workdir = fixture.Root })
            : JsonSerializer.SerializeToElement(new { script, workdir = fixture.Root });
        var result = await tools.Execute(new ToolExecutionInput
        {
            Name = repl ? "run_csharp_repl" : "run_csx",
            CallId = ToolCallId.Create("real-settings-frontend"),
            Arguments = arguments,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.False(result.IsError, string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
        var stdout = repl ? value.GetProperty("stdout").GetProperty("text").GetString() : value.GetProperty("stdout").GetString();
        var stderr = repl ? value.GetProperty("stderr").GetProperty("text").GetString() : value.GetProperty("stderr").GetString();
        Assert.Contains("denied 中文", stdout);
        Assert.Contains("frontend-stderr", stderr);
        Assert.Equal("workspace 中文", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "frontend-output"), TestContext.Current.CancellationToken));
        Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
    }
}
