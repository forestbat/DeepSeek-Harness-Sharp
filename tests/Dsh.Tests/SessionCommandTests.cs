using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

public sealed class SessionCommandTests
{
    [Fact]
    public async Task Rename_UpdatesCurrentSessionAndPersistsAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-session-cmd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ctx = new Context();
            _ = new AgentRegistry(ctx);
            var commands = CommandsService.Register(ctx);
            var agent = new FakeAgent(ctx);
            using (var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None))
            {
                using var registration = SessionCommand.Register(ctx, persistence);
                var handle = persistence.Create(agent.Session.Header);
                handle.Flush();
                handle.Close();

                var result = await commands.Execute(agent, "/rename New Title", TestContext.Current.CancellationToken);

                Assert.NotNull(result);
                Assert.IsType<CommandResult.Success>(result.Result);
                Assert.Equal("New Title", agent.Session.Header.Title);
                Assert.Equal("New Title", persistence.Stat(agent.Id)?.Header.Title);
                var reader = persistence.Open(agent.Id, SessionAccess.Read);
                Assert.Equal("New Title", reader.Header.Title);
                reader.Close();
            }

            using var reopened = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
            Assert.Equal("New Title", reopened.Stat(agent.Id)?.Header.Title);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SessionDelete_RemovesPersistedRecordAndRegistryEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-session-cmd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ctx = new Context();
            var registry = new AgentRegistry(ctx);
            var commands = CommandsService.Register(ctx);
            var current = new FakeAgent(ctx);
            var target = new FakeAgent(ctx);
            registry.Register(current);
            registry.Register(target);
            using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
            using var registration = SessionCommand.Register(ctx, persistence);
            foreach (var agent in new[] { current, target })
            {
                var handle = persistence.Create(agent.Session.Header);
                handle.Flush();
                handle.Close();
            }

            var result = await commands.Execute(current, $"/session delete {target.Id}", TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.IsType<CommandResult.Success>(result.Result);
            Assert.Null(persistence.Stat(target.Id));
            Assert.Null(registry.Get(target.Id));
            Assert.NotNull(persistence.Stat(current.Id));
            Assert.NotNull(registry.Get(current.Id));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /** 删除一个仍在 store 里的会话: 先释放会话(持久化正常收尾写句柄)再删记录, 不留错误日志, 也不会把日志写回已删除的位置。 */
    [Fact]
    public async Task SessionDelete_OfLiveSession_ReleasesItBeforeDeletingRecord()
    {
        var dir = TempTree.CreateDirectory("session-delete-live");
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir, PluginsDirectory: Path.Combine(dir, "plugins")), global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
            var registry = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
            var persistence = app.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
            var commands = app.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
            var current = (await registry.Create(new CreateAgentOptions(Cwd: dir), TestContext.Current.CancellationToken)).Agent;
            var target = (await registry.Create(new CreateAgentOptions(Cwd: dir), TestContext.Current.CancellationToken)).Agent;
            await sessions.Flush(target.Session);
            var errorsBefore = app.Ctx.Root.Logger.Buffer.Count(message => message.Type == LoggerType.Error);

            var result = await commands.Execute(current, $"/session delete {target.Id}", TestContext.Current.CancellationToken);

            Assert.IsType<CommandResult.Success>(result!.Result);
            Assert.Null(sessions.Get(target.Id));
            Assert.Null(persistence.Stat(target.Id));
            Assert.Equal(errorsBefore, app.Ctx.Root.Logger.Buffer.Count(message => message.Type == LoggerType.Error));
        }
        finally
        {
            TempTree.Delete(dir);
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
                IsSeeded = false
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
