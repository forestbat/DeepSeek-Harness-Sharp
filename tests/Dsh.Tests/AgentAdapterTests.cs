using Dsh.RemoteHost;
using Dsh.AgentAdapters;

namespace Dsh.Tests;

public sealed class AgentAdapterTests
{
    [Fact]
    public void RawTerminal_UsesOnlyItsExplicitCommand()
    {
        var terminal = AgentAdapterCatalog.Terminal("my agent", new AgentCommand("my-agent", ["--flag"]));

        Assert.Equal(new AgentCommand("my-agent", ["--flag"]), terminal.InteractiveCommand("ignored prompt"));
        Assert.Null(terminal.ResumeCommand("id"));
        Assert.Null(terminal.Acp);
    }

    [Fact]
    public void Commands_ExpandPromptAndResumeTemplates()
    {
        Assert.Equal(["fix the tests"], AgentAdapterCatalog.Codex.InteractiveCommand("fix the tests")!.Arguments);
        Assert.Equal(["resume", "abc"], AgentAdapterCatalog.Codex.ResumeCommand("abc")!.Arguments);
        Assert.Equal(["resume", "--last"], AgentAdapterCatalog.Codex.ResumeCommand()!.Arguments);
        Assert.Equal(["--resume", "abc"], AgentAdapterCatalog.ClaudeCode.ResumeCommand("abc")!.Arguments);
        Assert.Equal(["--continue"], AgentAdapterCatalog.ClaudeCode.ResumeCommand()!.Arguments);
        Assert.Equal(["--prompt", "fix the tests"], AgentAdapterCatalog.OpenCode.InteractiveCommand("fix the tests")!.Arguments);
        Assert.Equal(["--session", "ses_1"], AgentAdapterCatalog.OpenCode.ResumeCommand("ses_1")!.Arguments);
        var prompt = "fix the tests\n--session is prompt text, not an option";
        Assert.Equal(["--prompt", prompt], AgentAdapterCatalog.KiloCode.InteractiveCommand(prompt)!.Arguments);
        Assert.Equal(["--session", "ses_kilo"], AgentAdapterCatalog.KiloCode.ResumeCommand("ses_kilo")!.Arguments);
        Assert.Equal(["--continue"], AgentAdapterCatalog.KiloCode.ResumeCommand()!.Arguments);
    }

    [Fact]
    public void GenericAcp_HasNoInteractiveMode()
    {
        var agent = AgentAdapterCatalog.GenericAcp("Gemini", new AgentCommand("gemini", ["--experimental-acp"]));

        Assert.Equal((AgentKinds.Acp, (AgentCommand?)null), (agent.Kind, agent.InteractiveCommand("hi")));
        Assert.Equal("gemini", agent.Acp!.Executable);
    }

    [Fact]
    public void Locator_ResolvesExecutablesOnPathOnly()
    {
        Assert.NotNull(AgentExecutableLocator.Resolve("git"));
        Assert.Null(AgentExecutableLocator.Resolve($"dsh-missing-{Guid.NewGuid():N}"));
        Assert.Null(AgentExecutableLocator.Resolve(Path.Combine(AppContext.BaseDirectory, "missing", "agent")));
    }

    [Theory]
    [InlineData(AgentKinds.OpenCode)]
    [InlineData(AgentKinds.KiloCode)]
    public async Task Locator_ProbesInstalledAgentVersion(string agentKind)
    {
        var adapter = Assert.IsType<AgentAdapter>(AgentAdapterCatalog.Find(agentKind));
        if (AgentExecutableLocator.Resolve(adapter.Interactive!.Executable) is null)
            Assert.Skip($"{adapter.DisplayName} is not installed on this machine");

        var availability = await AgentExecutableLocator.ProbeAsync(adapter, TestContext.Current.CancellationToken);

        Assert.True(availability.CanRunInteractive && availability.CanRunAcp);
        Assert.Matches(@"^\d+\.\d+", availability.Version);
    }
}
