using Dsh.Runtime;
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
            using var app = await HarnessComposer.Compose(new HarnessOptions(new HarnessHome(home), Directory.GetCurrentDirectory()));
            var commands = app.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
            var agent = new FakeAgent(app.Ctx);

            var result = await commands.Execute(agent, "/provider add custom --base-url http://127.0.0.1:11434/v1 --api-key sk-test --model-ids custom-model", TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.IsType<CommandResult.Success>(result.Result);
            var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
            Assert.Contains(llm.ListProviders(), provider => provider.Id == "custom");
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
