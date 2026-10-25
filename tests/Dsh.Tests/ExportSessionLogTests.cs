using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Llm;

namespace Dsh.Tests;

/** 导出会话日志走 MainViewModel 的真实路径, 产物必须是每行一条事件的合法 JSONL。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class ExportSessionLogTests
{
    [Fact]
    public async Task ExportSessionLog_WritesEveryEventAsJsonLine() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        session.Append(new RequestHeaderPayload(
            new EpochHeader(new LlmCallConfig("test", "test-model")),
            RequestHeaderReasons.Initial));
        session.Append(new TurnStartPayload(1));
        session.Append(new AssistantMessagePayload(
            1,
            1,
            MessageFactory.CreateAssistantMessage([new TextBlock("回答")], "test", "test-model"),
            new TokenUsage(10, 5)),
            new SurfaceOp.Append());
        var persistence = environment.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
        await environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(session);
        using var stored = persistence.Open(environment.Agent.Id, SessionAccess.Read);
        var expected = stored.Read();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        await viewModel.ExportSessionLogCommand.ExecuteAsync(null);

        Assert.StartsWith("已导出", viewModel.StatusText);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(environment.App.Home.Root, "exports"), "*.jsonl"));
        var lines = await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken);
        Assert.Equal(expected.Count, lines.Length);
        foreach (var line in lines)
            Assert.NotNull(DshJson.Deserialize<SessionEvent>(line));
    });

}
