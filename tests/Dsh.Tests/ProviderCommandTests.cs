using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

public sealed class ProviderCommandTests
{
    [Fact]
    public async Task Edit_UpdatesProviderAndPreservesUnspecifiedFields()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "provider-test-home", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        HarnessApp? app = null;
        try
        {
            var home = new HarnessHome(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.yaml"), """
                providers:
                  local:
                    type: openai-compatible
                    baseUrl: http://127.0.0.1:1/v1
                    apiKey: old-key
                    modelIds: [m1]
                configs:
                  local:
                    provider: local
                    model: m1
                """);
            app = HarnessComposer.Compose(new HarnessOptions(home, Cwd: dir));
            var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var handle = await agents.Create(new CreateAgentOptions(
                SessionId.Create("session-provider-test"),
                dir,
                new AgentOptions("local", "m1")));
            var agent = (AgentLoopAgent)handle.Agent;
            var commands = app.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
            var execution = await commands.Execute(agent, "/provider edit local --base-url http://new/v1 --model-ids m2,m3");
            Assert.NotNull(execution);
            Assert.IsType<CommandResult.Success>(execution.Result);
            var settings = HarnessSettings.Load(home);
            var provider = settings.Providers["local"];
            Assert.Equal("http://new/v1", provider.BaseUrl);
            Assert.Equal("old-key", provider.ApiKey);
            Assert.Equal("openai-compatible", provider.Type);
            Assert.Equal(["m2", "m3"], provider.ModelIds);
        }
        finally
        {
            app?.Dispose();
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}