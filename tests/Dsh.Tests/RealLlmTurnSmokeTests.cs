using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Tests;

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
            await agent.WhenIdle().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
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
}
