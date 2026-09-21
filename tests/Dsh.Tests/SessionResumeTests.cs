using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Runtime;

namespace Dsh.Tests;

/** 恢复既有会话必须接管磁盘日志, 不能重建(重建会撞上 SessionAlreadyExistsException 或写重复事件)。 */
public sealed class SessionResumeTests
{
    [Fact]
    public async Task Resume_AppendsToExistingLogWithoutDuplicating()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dsh-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var homePath = PrepareHome(directory);
            var sessionId = SessionId.Create($"session-{Guid.NewGuid()}");
            int eventCount;
            using (var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(homePath), directory)))
            {
                var agent = await CreateSessionAsync(app, sessionId, directory);
                agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("hello")), new SurfaceOp.Append());
                eventCount = agent.Session.SnapshotEvents().Count;
                await app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(agent.Session);
            }

            using (var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(homePath), directory)))
            {
                var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
                var handle = await agents.Resume(new ResumeAgentOptions(sessionId, new AgentOptions("test", "test-model")), TestContext.Current.CancellationToken);
                var resumed = (AgentLoopAgent)handle.Agent;
                var resumedEvents = resumed.Session.SnapshotEvents();
                // 恢复会补一条 session/end-seed, 标记"这之前的都是被恢复的历史"。
                Assert.Equal(eventCount + 1, resumedEvents.Count);
                Assert.Equal(SessionEventTypes.SessionEndSeed, resumedEvents[^1].Type);
                resumed.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("again")), new SurfaceOp.Append());
                await app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(resumed.Session);
            }

            using var persistence = new JsonlSessionPersistence(Path.Combine(homePath, "sessions"));
            var reader = persistence.Open(sessionId, SessionAccess.Read);
            var stored = reader.Read();
            reader.Close();
            if (stored.Count != eventCount + 2)
                Assert.Fail($"stored={stored.Count} [{string.Join(",", stored.Select(sessionEvent => sessionEvent.Type))}], expected={eventCount + 2}");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Resume_RepairsInterruptedTurnWithoutDuplicatingTail()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dsh-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var homePath = PrepareHome(directory);
            var sessionId = SessionId.Create($"session-{Guid.NewGuid()}");
            int eventCount;
            using (var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(homePath), directory)))
            {
                var agent = await CreateSessionAsync(app, sessionId, directory);
                agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("hello")), new SurfaceOp.Append());
                agent.Session.Append(new TurnStartPayload(1));
                eventCount = agent.Session.SnapshotEvents().Count;
                await app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(agent.Session);
            }

            using (var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(homePath), directory)))
            {
                var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
                var handle = await agents.Resume(new ResumeAgentOptions(sessionId, new AgentOptions("test", "test-model")), TestContext.Current.CancellationToken);
                var resumed = (AgentLoopAgent)handle.Agent;
                var events = resumed.Session.SnapshotEvents();
                // 中断轮先补收尾事件, 再补 session/end-seed 边界。
                Assert.Equal(eventCount + 2, events.Count);
                Assert.IsType<TurnEndPayload>(events[^2].Data);
                Assert.IsType<SessionEndSeedPayload>(events[^1].Data);
                await app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(resumed.Session);
            }

            using var persistence = new JsonlSessionPersistence(Path.Combine(homePath, "sessions"));
            var reader = persistence.Open(sessionId, SessionAccess.Read);
            var stored = reader.Read();
            Assert.Equal(eventCount + 2, stored.Count);
            Assert.IsType<TurnEndPayload>(stored[^2].Data);
            reader.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /** 关掉文件日志: 同一 home 连续 compose 两次时, 日志文件句柄的释放时机不受本测试控制。 */
    private static string PrepareHome(string directory)
    {
        var homePath = Path.Combine(directory, "home");
        Directory.CreateDirectory(homePath);
        File.WriteAllText(Path.Combine(homePath, "settings.yaml"), GuiTestEnvironment.HomeSettings);
        return homePath;
    }

    private static async Task<AgentLoopAgent> CreateSessionAsync(HarnessApp app, SessionId sessionId, string cwd)
    {
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(sessionId, cwd, new AgentOptions("test", "test-model")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        return agent;
    }
}
