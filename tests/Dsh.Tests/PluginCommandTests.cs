using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class PluginCommandTests
{
    [Fact]
    public async Task PluginsList_ShowsLoadFailureGroup_AndDoctorExplainsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-plugins-cmd-{Guid.NewGuid():N}");
        var plugins = Path.Combine(root, "plugins");
        Directory.CreateDirectory(Path.Combine(plugins, "bogus"));
        await File.WriteAllTextAsync(Path.Combine(plugins, "bogus", "bogus.dll"), "not a plugin",
            TestContext.Current.CancellationToken);
        try
        {
            using var app = await HarnessComposer.Compose(new HarnessOptions(
                new HarnessHome(Path.Combine(root, "home")), root, PluginsDirectory: plugins));
            var commands = app.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
            var agent = new FakeAgent(app.Ctx);

            var list = await commands.Execute(agent, "/plugins list", TestContext.Current.CancellationToken);
            var listText = Assert.IsType<CommandResult.Success>(list!.Result).Text ?? "";
            Assert.Contains("加载失败", listText);
            Assert.Contains("bogus.dll", listText);

            var doctor = await commands.Execute(agent, "/plugins doctor", TestContext.Current.CancellationToken);
            var doctorText = Assert.IsType<CommandResult.Success>(doctor!.Result).Text ?? "";
            Assert.Contains("bogus.dll", doctorText);
            Assert.Contains("排查顺序", doctorText);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
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
