using Avalonia.Threading;
using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Llm;

namespace Dsh.Tests;

/** 导出会话日志走 MainViewModel 的真实路径, 产物必须是每行一条事件的合法 JSONL。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class ExportSessionLogTests
{
    [Fact]
    public async Task ExportSessionLog_WritesEveryEventAsJsonLine()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
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
        var expected = await WaitForStoredEvents(persistence, environment.Agent.Id, 3);
        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        viewModel.ExportSessionLogCommand.Execute(null);
        for (var attempt = 0; attempt < 500 && !viewModel.StatusText.Contains("导出"); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Assert.StartsWith("已导出", viewModel.StatusText);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(environment.App.Home.Root, "exports"), "*.jsonl"));
        var lines = await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken);
        Assert.Equal(expected.Count, lines.Length);
        foreach (var line in lines)
            Assert.NotNull(DshJson.Deserialize<SessionEvent>(line));
    }

    private static async Task<IReadOnlyList<SessionEvent>> WaitForStoredEvents(ISessionPersistence persistence, SessionId id, int count)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            using var handle = persistence.Open(id, SessionAccess.Read);
            var events = handle.Read();
            if (events.Count >= count)
                return events;
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        throw new TimeoutException($"session \"{id}\" 持久化事件数未达到 {count}");
    }
}
