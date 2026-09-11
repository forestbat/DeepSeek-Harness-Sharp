using Cordis;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

public sealed class ProviderCommandTests
{
    [Fact]
    public async Task ProviderAdd_FirstModelSetsGlobalDefault()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-provider-cmd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            File.WriteAllText(Path.Combine(home, "settings.yaml"), """
                global_default_model: null
                providers: {}
                """);
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            _ = new LlmRuntime(ctx);
            var commands = CommandsService.Register(ctx);
            using var registration = ProviderCommand.Register(ctx, new HarnessHome(home));
            var agent = new FakeAgent(ctx);

            var result = await commands.Execute(agent, "/provider add custom --base-url http://127.0.0.1:11434/v1 --api-key sk-test --model-ids custom-model");

            Assert.NotNull(result);
            Assert.IsType<CommandResult.Success>(result.Result);
            var settings = HarnessSettings.Load(new HarnessHome(home));
            Assert.Equal("custom/custom-model", settings.GlobalDefaultModel);
            Assert.True(settings.Providers["custom"].Models.ContainsKey("custom-model"));
        }
        finally
        {
            Directory.Delete(home, true);
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
        public List<UserMessage> Injected { get; } = [];

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

        public void Inject(UserMessage message) => Injected.Add(message);
    }
}
