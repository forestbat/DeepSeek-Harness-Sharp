using Cordis;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

public sealed class MemoryCommandTests
{
    [Fact]
    public async Task ToggleOnOff_PersistsSettingAndChangesPrompt()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-memory-cmd", Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDir);
        var home = new HarnessHome(Path.Combine(root, "home"));
        try
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var commands = CommandsService.Register(ctx);
            var options = new HarnessOptions(home, Cwd: projectDir);
            using var registration = MemoryCommand.Register(ctx, options);
            var agent = new FakeAgent(ctx);

            var enabled = await commands.Execute(agent, "/memory on");
            Assert.NotNull(enabled);
            Assert.IsType<CommandResult.Success>(enabled.Result);
            Assert.True(HarnessSettings.Load(home).Memory?.Enabled == true);

            var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
            var assembly = await systemPrompt.Assemble(new AssembleContext());
            var prompt = PromptRender.RenderPrompt(assembly);
            Assert.Contains("Project memory is enabled", prompt);
            Assert.Contains(".dsh-memory.md", prompt);
            Assert.Contains(assembly.Contexts, context => context.Text.Contains("Project memory"));

            var disabled = await commands.Execute(agent, "/memory off");
            Assert.NotNull(disabled);
            Assert.IsType<CommandResult.Success>(disabled.Result);
            Assert.False(HarnessSettings.Load(home).Memory?.Enabled == true);

            assembly = await systemPrompt.Assemble(new AssembleContext());
            Assert.DoesNotContain("Project memory is enabled", PromptRender.RenderPrompt(assembly));
            Assert.DoesNotContain(assembly.Contexts, context => context.Text.Contains("Project memory"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RejectsUnknownOption()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-memory-cmd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var home = new HarnessHome(Path.Combine(root, "home"));
        try
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var commands = CommandsService.Register(ctx);
            using var registration = MemoryCommand.Register(ctx, new HarnessOptions(home, Cwd: root));
            var agent = new FakeAgent(ctx);

            var result = await commands.Execute(agent, "/memory maybe");

            Assert.NotNull(result);
            Assert.IsType<CommandResult.Error>(result.Result);
            Assert.False(HarnessSettings.Load(home).Memory?.Enabled == true);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(Context ctx)
        {
            Ctx = ctx;
            var id = SessionId.Create($"session-{Guid.NewGuid():N}");
            Session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 0,
                Cwd = Path.GetTempPath(),
                IsSeeded = false,
            });
        }

        public SessionId Id => Session.Id;
        public Session Session { get; }
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; }
        public AgentStatus Status => AgentStatus.Idle;
        public AgentOptions Options { get; } = new();

        public void Cancel(AgentCancelCause cause, bool keepInbox = false)
        {
        }

        public Task WhenIdle() => Task.CompletedTask;

        public void Send(UserMessage message, string target, bool wakeup)
        {
        }

        public void Followup(UserMessage message)
        {
        }

        public void Steer(UserMessage message)
        {
        }

        public void Inject(UserMessage message)
        {
        }
    }
}