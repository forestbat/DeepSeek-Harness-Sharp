using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Schedule;

namespace Dsh.Tests;

[Trait("Category", "LlmSmoke")]
public sealed class RealLlmTurnSmokeTests
{
    [Fact]
    public async Task RealLlm_AgentTurnCompletes()
    {
        var settings = HarnessSettings.Load(HarnessHome.Resolve());
        var providerId = Environment.GetEnvironmentVariable("DSH_REAL_LLM_PROVIDER") ?? "deepseek-official";
        var modelId = Environment.GetEnvironmentVariable("DSH_REAL_LLM_MODEL") ?? "deepseek-v4-flash";
        if (!settings.Providers.TryGetValue(providerId, out var provider)
            || string.IsNullOrEmpty(provider.Options?.ApiKey))
            return;

        var home = Path.Combine(Path.GetTempPath(), $"dsh-real-llm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            File.Copy(Path.Combine(HarnessHome.Resolve().Root, "settings.yaml"), Path.Combine(home, "settings.yaml"), overwrite: true);
            var options = new HarnessOptions(
                HarnessHome.Resolve(home),
                Cwd: Directory.GetCurrentDirectory(),
                Provider: providerId,
                Model: modelId);
            using var app = await HarnessComposer.Compose(options);
            var failures = new List<string>();
            app.Ctx.On<AgentErrorNotification>(notification =>
            {
                failures.Add(notification.Error.ToString());
            }, new EventOptions { Global = true });
            var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var handle = await agents.Create(new CreateAgentOptions(
                SessionId.Create($"smoke-{Guid.NewGuid():N}"),
                Directory.GetCurrentDirectory(),
                new AgentOptions(providerId, modelId)), TestContext.Current.CancellationToken);
            var agent = (AgentLoopAgent)handle.Agent;
            agent.Followup(MessageFactory.CreateUserText("只回复两个字:收到"));
            await agent.WhenIdle().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var events = agent.Session.SnapshotEvents();
            var assistant = events
                .Select(sessionEvent => sessionEvent.Data)
                .OfType<AssistantMessagePayload>()
                .ToList();
            Assert.True(assistant.Count > 0,
                $"session events: {string.Join(", ", events.Select(sessionEvent => sessionEvent.Data.Type))}; failures: {string.Join(" | ", failures)}");
            Assert.Contains(assistant, payload => payload.Message.Content.Count > 0);
        }
        finally
        {
            try
            {
                Directory.Delete(home, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealLlm_ScheduleDeliversOnceAcrossLiveAndColdSessions(bool coldResume)
    {
        var sourceHome = HarnessHome.Resolve();
        var settings = HarnessSettings.Load(sourceHome);
        var route = settings.ResolveDefaultModel();
        Assert.NotNull(route);
        var (providerId, modelId) = route.Value;
        Assert.True(settings.Providers.TryGetValue(providerId, out var provider)
            && (!string.IsNullOrEmpty(provider.Options?.ApiKey)
                || provider.Options?.ApiKeyEnv is { Length: > 0 } keyName && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(keyName))),
            "The schedule smoke requires credentials for the configured default model.");
        var directory = TempTree.CreateDirectory("dsh-real-schedule");
        var home = Path.Combine(directory, "home");
        Directory.CreateDirectory(home);
        File.Copy(Path.Combine(sourceHome.Root, "settings.yaml"), Path.Combine(home, "settings.yaml"));
        var sessionId = SessionId.Create($"schedule-smoke-{Guid.NewGuid():N}");
        var options = new HarnessOptions(HarnessHome.Resolve(home), directory, Provider: providerId, Model: modelId);
        HarnessApp? app = null;
        try
        {
            app = await HarnessComposer.Compose(options);
            var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var agentOptions = new AgentOptions(providerId, modelId);
            var agent = (await agents.Create(new CreateAgentOptions(sessionId, directory, agentOptions), TestContext.Current.CancellationToken)).Agent;
            var schedules = app.Ctx.Get<ScheduleService>(ScheduleService.ServiceName)!;
            schedules.Create(agent, "real reminder", "Reply with exactly SCHEDULE_SMOKE_OK. Do not call tools.", new ScheduleTiming.After(2));
            await app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(agent.Session);
            if (coldResume)
            {
                app.Dispose();
                app = null;
                await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                app = await HarnessComposer.Compose(options);
                agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
                agent = (await agents.Resume(new ResumeAgentOptions(sessionId, agentOptions), TestContext.Current.CancellationToken)).Agent;
            }

            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!agent.Session.SnapshotEvents().Any(sessionEvent => sessionEvent.Data is AssistantMessagePayload)
                && DateTime.UtcNow < deadline)
                await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await agent.WhenIdle().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var events = agent.Session.SnapshotEvents();
            Assert.Single(events, sessionEvent => sessionEvent.Data is ScheduleChangePayload { Change.Operation: ScheduleOperation.Dispatch });
            Assert.Single(events, sessionEvent => sessionEvent.Data is UserMessagePayload { Message.Source: PluginMessageSource { Plugin: ScheduleService.MessagePlugin } });
            Assert.Contains(events.Select(sessionEvent => sessionEvent.Data).OfType<AssistantMessagePayload>(),
                payload => payload.Message.Content.OfType<TextBlock>().Any(block => block.Text.Contains("SCHEDULE_SMOKE_OK", StringComparison.Ordinal)));
            Assert.Empty(ScheduleLedger.Of(agent.Session).Active);
            await app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(agent.Session);
            app.Dispose();
            app = await HarnessComposer.Compose(options);
            agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var resumed = (await agents.Resume(new ResumeAgentOptions(sessionId, agentOptions), TestContext.Current.CancellationToken)).Agent;
            await resumed.WhenIdle().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Empty(ScheduleLedger.Of(resumed.Session).Active);
            Assert.Single(resumed.Session.SnapshotEvents(), sessionEvent =>
                sessionEvent.Data is ScheduleChangePayload { Change.Operation: ScheduleOperation.Dispatch });
        }
        finally
        {
            app?.Dispose();
            TempTree.Delete(directory);
        }
    }
}
