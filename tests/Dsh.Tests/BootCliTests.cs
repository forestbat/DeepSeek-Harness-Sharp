using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

public sealed class BootCliTests
{
    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Headless_WithoutConfiguredModel_PersistsFailureForNewAndResumedSessions(bool resume)
    {
        var root = TempTree.CreateDirectory("headless-configuration");
        try
        {
            var home = new HarnessHome(root);
            await File.WriteAllTextAsync(home.SettingsFile, """
                logging:
                  file: false
                plugins:
                  "@deepseek-ai/dsh-checkpoints": false
                """, TestContext.Current.CancellationToken);
            var resumedId = resume ? await CreatePersistedSession(home) : (SessionId?)null;

            var exitCode = await BootCli.RunHeadlessAsync(home, "Explain this project.", resumedId?.Value,
                global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);

            Assert.Equal(1, exitCode);
            using var persistence = new JsonlSessionPersistence(home.SessionsPath);
            var snapshot = Assert.Single(persistence.List());
            if (resumedId is { } expectedId)
                Assert.Equal(expectedId, snapshot.Header.Id);
            using var session = persistence.Open(snapshot.Header.Id, SessionAccess.Read);
            var events = session.Read();
            Assert.Contains(events, item => item.Data is UserMessagePayload user
                && user.Message.Content.OfType<TextBlock>().Any(block => block.Text == "Explain this project."));
            var failure = Assert.Single(events.Select(item => item.Data).OfType<TurnEndPayload>());
            var error = Assert.IsType<TurnEndReason.Error>(failure.Reason);
            Assert.Equal("UNKNOWN", error.Failure.Code);
            Assert.Contains("no LLM provider/model is configured", error.Failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(events, item => item.Data is RequestHeaderPayload or AssistantMessagePayload);
            if (resume)
                Assert.Contains(events, item => item.Data is UserMessagePayload user
                    && user.Message.Content.OfType<TextBlock>().Any(block => block.Text == "Earlier request."));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    private static async Task<SessionId> CreatePersistedSession(HarnessHome home)
    {
        using var app = await ConfigBoot.Compose(new HarnessOptions(home),
            global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
        var handle = await app.Ctx.Get<AgentRegistry>()!.Create(new CreateAgentOptions(Cwd: home.Root),
            TestContext.Current.CancellationToken);
        using var ownership = handle.Dispose;
        await handle.Agent.WhenIdle();
        handle.Agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("Earlier request.")), new SurfaceOp.Append());
        await app.Ctx.Get<SessionStore>()!.Flush(handle.Agent.Session);
        return handle.Agent.Id;
    }
}
