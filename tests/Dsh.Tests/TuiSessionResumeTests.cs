using System.Diagnostics;
using System.Runtime.InteropServices;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

public sealed class TuiSessionResumeTests
{
    private const string History = "resume-visible-history";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("tui", "--session", "{id}")]
    [InlineData("--session", "{id}")]
    [InlineData("--session", "{id}", "tui")]
    [InlineData("tui", "attach", "{id}")]
    public async Task OpensRequestedHistoryWithoutCreatingAnotherSession(params string[] arguments)
    {
        var directory = TempTree.CreateDirectory("tui-resume");
        try
        {
            var home = new HarnessHome(Path.Combine(directory, "home"));
            var sessionId = await SaveSessionAsync(home, directory);
            var result = await RunAsync(home, arguments.Select(argument => argument == "{id}" ? sessionId.ToString() : argument).ToArray());
            Assert.True(result.ExitCode == 0, result.Error);
            Assert.Contains(History, result.Output);
            using var persistence = new JsonlSessionPersistence(home.SessionsPath);
            Assert.Equal(sessionId, Assert.Single(persistence.List()).Header.Id);
        }
        finally
        {
            TempTree.Delete(directory);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedResumeReportsErrorWithoutCreatingReplacement(bool corruptLog, bool gpu)
    {
        var directory = TempTree.CreateDirectory("tui-resume-failure");
        try
        {
            var home = new HarnessHome(Path.Combine(directory, "home"));
            var sessionId = await SaveSessionAsync(home, directory);
            var log = Assert.Single(Directory.GetFiles(home.SessionsPath, "session.jsonl*", SearchOption.AllDirectories));
            if (corruptLog)
                await File.WriteAllTextAsync(log, "invalid session log", TestContext.Current.CancellationToken);
            else
                File.Delete(log);
            var before = Directory.GetDirectories(home.SessionsPath, "*", SearchOption.AllDirectories).Order().ToArray();
            var arguments = new List<string> { "tui", "--session", sessionId.ToString() };
            if (gpu)
                arguments.AddRange(["--gpu", "--gpu-screenshot", Path.Combine(directory, "failure.png")]);
            var result = await RunAsync(home, arguments.ToArray());
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains($"dsharp: session \"{sessionId}\" cannot be resumed:", result.Error);
            Assert.Equal(before, Directory.GetDirectories(home.SessionsPath, "*", SearchOption.AllDirectories).Order().ToArray());
        }
        finally
        {
            TempTree.Delete(directory);
        }
    }

    private static async Task<SessionId> SaveSessionAsync(HarnessHome home, string directory)
    {
        Directory.CreateDirectory(home.Root);
        await File.WriteAllTextAsync(home.SettingsFile, GuiTestEnvironment.HomeSettings, TestContext.Current.CancellationToken);
        using var app = await ConfigBoot.Compose(new HarnessOptions(home, directory));
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var sessionId = SessionId.Create("session-resume-history");
        var handle = await agents.Create(new CreateAgentOptions(sessionId, directory, new AgentOptions("test", "test-model")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText(History)), new SurfaceOp.Append());
        await app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(agent.Session);
        return sessionId;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(HarnessHome home, string[] arguments)
    {
        var framework = new DirectoryInfo(AppContext.BaseDirectory);
        var assembly = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../DeepSeek-Harness-Sharp/bin", framework.Parent!.Name, framework.Name, "dsharp.dll"));
        Assert.True(File.Exists(assembly), "The CLI project reference must build dsharp before running its component tests.");
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(home.Root)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment[HarnessHome.ChildHomeEnv] = home.Root;
        using var process = TestProcessScope.Start(start)!;
        process.StandardInput.Close();
        return await ReadResultAsync(process);
    }

    private static async Task<(int ExitCode, string Output, string Error)> ReadResultAsync(Process process)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(ProcessTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
