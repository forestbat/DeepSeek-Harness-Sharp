using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dsh.CSharpRepl;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class CSharpReplToolsTests
{
    [Fact(Timeout = 60000)]
    public async Task MultilineProgram_PreservesStateAndPrintsFinalExpression()
    {
        using var harness = await Harness.CreateAsync();
        var result = await harness.RunAsync("""
            var values = new System.Collections.Generic.List<int> { 2, 3 };
            values.Add(7);
            int Sum() => System.Linq.Enumerable.Sum(values);
            await Console.Out.WriteLineAsync($"frontend-state:{Sum()}");
            Sum() * 2
            """, TestContext.Current.CancellationToken);

        var value = Success(result);
        var stdout = value.GetProperty("stdout").GetProperty("text").GetString()!;
        Assert.Contains("frontend-state:12", stdout);
        Assert.Equal("24", stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Trim());
    }

    [Fact(Timeout = 60000)]
    public async Task RelativeExecutablePathIsResolvedFromHostDirectory()
    {
        using var harness = await Harness.CreateAsync(relativeExecutable: true);
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "relative-frontend.txt"), "relative-command-working-directory", TestContext.Current.CancellationToken);

        var result = await harness.RunAsync("""await System.IO.File.ReadAllTextAsync("relative-frontend.txt")""");

        var value = Success(result);
        Assert.Contains("relative-command-working-directory", value.GetProperty("stdout").GetProperty("text").GetString());
    }

    [Fact(Timeout = 60000)]
    public async Task OutputStreamsAndUnicode_AreCapturedFromFrontend()
    {
        using var harness = await Harness.CreateAsync();
        var result = await harness.RunAsync("""
            await Console.Out.WriteLineAsync("real-output-中文");
            await Console.Error.WriteLineAsync("real-error-stream");
            """, TestContext.Current.CancellationToken);

        var value = Success(result);
        Assert.Contains("real-output-中文", value.GetProperty("stdout").GetProperty("text").GetString());
        Assert.Contains("real-error-stream", value.GetProperty("stderr").GetProperty("text").GetString());
    }

    [Fact(Timeout = 60000)]
    public async Task SeparateCalls_DoNotClaimPersistentState()
    {
        using var harness = await Harness.CreateAsync();
        Success(await harness.RunAsync("int submittedOnly = 19; submittedOnly", TestContext.Current.CancellationToken));

        var result = await harness.RunAsync("submittedOnly + 1", TestContext.Current.CancellationToken);

        Failure(result, "CSHARP_REPL_FAILED");
        Assert.Contains("submittedOnly", Text(result));
        Assert.Contains("CS0103", Text(result));
    }

    [Fact(Timeout = 60000)]
    public async Task InvalidSyntax_ReturnsFrontendDiagnosticsAsFailure()
    {
        using var harness = await Harness.CreateAsync();
        var result = await harness.RunAsync("int invalidValue = ;", TestContext.Current.CancellationToken);

        Failure(result, "CSHARP_REPL_FAILED");
        Assert.Contains("CS1525", Text(result));
    }

    [Theory(Timeout = 60000)]
    [InlineData("System.InvalidOperationException")]
    [InlineData("System.Exception")]
    public async Task RuntimeException_PreservesPriorOutputAndMessage(string exceptionType)
    {
        using var harness = await Harness.CreateAsync();
        var result = await harness.RunAsync($$"""
            await Console.Out.WriteLineAsync("before-frontend-exception");
            throw new {{exceptionType}}("real-frontend-runtime-failure");
            """, TestContext.Current.CancellationToken);

        Failure(result, "CSHARP_REPL_FAILED");
        Assert.Contains("before-frontend-exception", Text(result));
        Assert.Contains("real-frontend-runtime-failure", Text(result));
        if (exceptionType == "System.Exception")
        {
            Assert.Contains("managedExitCode: -2146233088", Text(result));
            if (!OperatingSystem.IsWindows())
                Assert.Contains("[exitCode: 0; managedExitCode: -2146233088]", Text(result));
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Cancellation_AfterEvaluationStarts_ReapsOwnedProcess()
    {
        using var harness = await Harness.CreateAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = harness.RunAsync(BlockingProgram, cancellation.Token);
        try
        {
            var pid = await harness.WaitForPidAsync(pending);
            cancellation.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Failure(result, ToolErrorCodes.Aborted);
            AssertExited(pid);
        }
        finally
        {
            cancellation.Cancel();
            await pending;
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Timeout_ReapsEvaluatingFrontendAndReturnsFailure()
    {
        using var harness = await Harness.CreateAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = harness.RunAsync(BlockingProgram, cancellation.Token, timeoutMs: 30_000);
        try
        {
            var pid = await harness.WaitForPidAsync(pending);
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(40), TestContext.Current.CancellationToken);

            Failure(result, "CSHARP_REPL_TIMEOUT");
            AssertExited(pid);
        }
        finally
        {
            cancellation.Cancel();
            await pending;
        }
    }

    [Fact(Timeout = 60000)]
    public async Task MissingExecutable_IsInvocationFailureAndLeavesOtherFrontendUsable()
    {
        using var available = await Harness.CreateAsync();
        using var missing = new Harness(new CSharpReplConfig
        {
            Executable = Path.Combine(available.Root, "not-installed-csharprepl"),
        });

        var result = await missing.RunAsync("1 + 1", TestContext.Current.CancellationToken);

        Failure(result, "CSHARP_REPL_MISSING");
        Assert.Equal("42", Success(await available.RunAsync("6 * 7", TestContext.Current.CancellationToken)).GetProperty("stdout").GetProperty("text").GetString()?.Trim());
    }

    private const string BlockingProgram = """
        await System.IO.File.WriteAllTextAsync("frontend.pid", System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite);
        """;

    private static JsonElement Success(ToolExecutionResult result)
    {
        Assert.False(result.IsError, Text(result));
        var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
        Assert.Equal(0, value.GetProperty("exitCode").GetInt32());
        Assert.Equal(0, value.GetProperty("managedExitCode").GetInt32());
        return value;
    }

    private static void Failure(ToolExecutionResult result, string code)
    {
        Assert.True(result.IsError, Text(result));
        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Equal(code, failure.Error.Info?.Code);
    }

    private static string Text(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    private static void AssertExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, "The owned CSharpRepl process must exit before tool completion.");
        }
        catch (ArgumentException)
        {
            // A reaped process no longer has a process-table entry.
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly Context _ctx = new();
        private readonly SubprocessService _subprocess;
        private readonly ToolRuntime _tools;
        private IDisposable? _registration;

        public Harness(CSharpReplConfig? config)
        {
            Root = TempTree.CreateDirectory("csharprepl-tools");
            _ = new SystemPrompt(_ctx, new SystemPromptConfig());
            _tools = new ToolRuntime(_ctx);
            _subprocess = new SubprocessService(_ctx);
            if (config is not null)
                _registration = new Dsh.CSharpRepl.Plugin().Apply(_ctx, config);
        }

        public string Root { get; }

        public static async Task<Harness> CreateAsync(bool relativeExecutable = false)
        {
            var harness = new Harness(null);
            try
            {
                var executable = await harness._subprocess.ResolveExecutable("csharprepl", signal: TestContext.Current.CancellationToken);
                var config = new CSharpReplConfig
                {
                    Executable = relativeExecutable ? Path.GetRelativePath(Environment.CurrentDirectory, executable) : "csharprepl",
                };
                harness._registration = new Dsh.CSharpRepl.Plugin().Apply(harness._ctx, config);
                return harness;
            }
            catch (FileNotFoundException)
            {
                harness.Dispose();
                Assert.Skip("CSharpRepl is not installed on PATH; install the genuine optional CSharpRepl frontend to run these tests.");
                throw;
            }
        }

        public Task<ToolExecutionResult> RunAsync(string code, CancellationToken? signal = null, long? timeoutMs = null)
        {
            var arguments = JsonSerializer.SerializeToElement(new
            {
                code,
                description = "Evaluate real frontend program",
                workdir = Root,
                timeoutMs = timeoutMs ?? 45_000,
            });
            return _tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = ToolRunCSharpRepl.ToolName,
                Arguments = arguments,
                Signal = signal ?? TestContext.Current.CancellationToken,
            });
        }

        public async Task<int> WaitForPidAsync(Task<ToolExecutionResult> pending)
        {
            var path = Path.Combine(Root, "frontend.pid");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path, deadline.Token), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                    return pid;
                if (pending.IsCompleted)
                    Assert.Fail("Frontend exited before reaching the blocking evaluation: " + Text(await pending));
                await Task.Delay(20, deadline.Token);
            }
        }

        public void Dispose()
        {
            _registration?.Dispose();
            _subprocess.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
