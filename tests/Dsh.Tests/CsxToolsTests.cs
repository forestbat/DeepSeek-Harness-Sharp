using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dsh.Core;
using Dsh.Csx;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class CsxToolsTests
{
    [Fact]
    public async Task ScriptFilePreservesLoadsArgumentsWorkingDirectoryAndStreams()
    {
        using var harness = new Harness();
        var executable = await harness.RequireFrontend();
        using var registration = ToolRunCsx.Register(harness.Ctx, harness.Root, new CsxPluginConfig { Executable = executable });
        var scripts = Directory.CreateDirectory(Path.Combine(harness.Root, "scripts")).FullName;
        await File.WriteAllTextAsync(Path.Combine(scripts, "library.csx"), "int Add(int a, int b) => a + b;\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(scripts, "main.csx"), """
            #load "library.csx"
            await Console.Out.WriteLineAsync(Add(19, 23).ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Console.Out.WriteLineAsync(string.Join("|", Args));
            await File.WriteAllTextAsync("arguments.txt", string.Join("|", Args));
            await Console.Out.WriteLineAsync(await File.ReadAllTextAsync("working-directory.txt"));
            await Console.Error.WriteLineAsync("csx-stderr 中文");
            """, TestContext.Current.CancellationToken);
        var workdir = Directory.CreateDirectory(Path.Combine(harness.Root, "work")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workdir, "working-directory.txt"), "csx-cwd", TestContext.Current.CancellationToken);

        var result = await harness.Execute(new
        {
            script = Path.Combine("..", "scripts", "main.csx"),
            workdir = "work",
            arguments = new[] { "two words", "--literal", "中文", "", "$(not-a-shell)" },
        });

        Assert.False(result.IsError, TextOf(result));
        var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
        Assert.Equal(0, value.GetProperty("exitCode").GetInt32());
        Assert.Equal("42\ntwo words|--literal|中文||$(not-a-shell)\ncsx-cwd\n", Normalize(value.GetProperty("stdout").GetString()!));
        Assert.Equal("two words|--literal|中文||$(not-a-shell)", await File.ReadAllTextAsync(Path.Combine(workdir, "arguments.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("csx-stderr 中文\n", Normalize(value.GetProperty("stderr").GetString()!));
        Assert.False(value.GetProperty("stdoutTruncated").GetBoolean());
        Assert.False(value.GetProperty("stderrTruncated").GetBoolean());
    }

    [Fact]
    public async Task CompilationFailureReportsRealFrontendDiagnostics()
    {
        using var harness = new Harness();
        await harness.RequireFrontend();
        using var registration = ToolRunCsx.Register(harness.Ctx, harness.Root);
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "invalid.csx"), "Console.WriteLine(UnknownCsxIdentifier);\n", TestContext.Current.CancellationToken);

        var result = await harness.Execute(new { script = "invalid.csx" });

        Assert.True(result.IsError);
        Assert.Contains("CS0103", TextOf(result));
        Assert.Contains("UnknownCsxIdentifier", TextOf(result));
    }

    [Fact]
    public async Task NonzeroExitRetainsBothStreamsAndActualExitStatus()
    {
        using var harness = new Harness();
        await harness.RequireFrontend();
        using var registration = ToolRunCsx.Register(harness.Ctx, harness.Root);
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "exit.csx"), """
            await Console.Out.WriteLineAsync("before-exit");
            await Console.Error.WriteLineAsync("exit-diagnostic");
            Environment.ExitCode = 7;
            """, TestContext.Current.CancellationToken);

        var result = await harness.Execute(new { script = "exit.csx" });

        Assert.True(result.IsError);
        Assert.Contains("before-exit", TextOf(result));
        Assert.Contains("exit-diagnostic", TextOf(result));
    }

    [Fact]
    public async Task CancellationWaitsForOwnedFrontendToExit()
    {
        using var harness = new Harness();
        await harness.RequireFrontend();
        using var registration = ToolRunCsx.Register(harness.Ctx, harness.Root);
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "wait.csx"), """
            await File.WriteAllTextAsync(Args[0], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Task.Delay(System.Threading.Timeout.Infinite);
            """, TestContext.Current.CancellationToken);
        var marker = Path.Combine(harness.Root, "ready.pid");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = harness.Execute(new { script = "wait.csx", arguments = new[] { marker } }, cancellation.Token);
        try
        {
            using var arrival = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            arrival.CancelAfter(TimeSpan.FromSeconds(60));
            int pid;
            while (!File.Exists(marker) || !int.TryParse(await File.ReadAllTextAsync(marker, arrival.Token), CultureInfo.InvariantCulture, out pid))
            {
                if (running.IsCompleted)
                    Assert.Fail($"dotnet-script exited before entering the script: {TextOf(await running)}");
                await Task.Delay(25, arrival.Token);
            }
            using var frontend = Process.GetProcessById(pid);
            cancellation.Cancel();

            var result = await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.True(result.IsError);
            Assert.Equal(ToolErrorCodes.Aborted, Assert.IsType<ToolExecutionResult.Failure>(result).Error.Info?.Code);
            Assert.True(frontend.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AbsentOptionalFrontendDoesNotPreventRegistrationButInvocationFails()
    {
        using var harness = new Harness();
        using var registration = new Dsh.Csx.Plugin().Apply(harness.Ctx, new CsxPluginConfig
        {
            Executable = Path.Combine(harness.Root, "missing-dotnet-script"),
        });
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "main.csx"), "await Console.Out.WriteLineAsync(\"42\");\n", TestContext.Current.CancellationToken);

        var result = await harness.Execute(new { script = Path.Combine(harness.Root, "main.csx") });

        Assert.True(result.IsError);
        Assert.Equal("CSX_FRONTEND_UNAVAILABLE", Assert.IsType<ToolExecutionResult.Failure>(result).Error.Info?.Code);
    }

    private static string TextOf(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Root = TempTree.CreateDirectory("csx-tools");
            Ctx = new Context();
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Subprocess = new SubprocessService(Ctx);
        }

        public string Root { get; }
        public Context Ctx { get; }
        public ToolRuntime Tools { get; }
        public SubprocessService Subprocess { get; }

        public async Task<string> RequireFrontend()
        {
            try
            {
                return await Subprocess.ResolveExecutable("dotnet-script", signal: TestContext.Current.CancellationToken);
            }
            catch (FileNotFoundException)
            {
                Assert.Skip("Optional dotnet-script frontend is not installed on PATH.");
                throw;
            }
        }

        public Task<ToolExecutionResult> Execute(object arguments, CancellationToken? signal = null)
            => Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"csx-{Guid.NewGuid():N}"),
                Name = ToolRunCsx.ToolName,
                Arguments = JsonSerializer.SerializeToElement(arguments),
                Signal = signal ?? TestContext.Current.CancellationToken,
            });

        public void Dispose()
        {
            Subprocess.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
