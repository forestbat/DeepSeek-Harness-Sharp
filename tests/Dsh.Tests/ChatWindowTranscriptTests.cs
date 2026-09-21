using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tui;

namespace Dsh.Tests;

/** TUI 会话流渲染: 历史回放、重复渲染守卫、非流式补渲染、审批提示参数。 */
public sealed class ChatWindowTranscriptTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/test-homes")),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Constructor_Replays_Existing_History_Once()
    {
        var (ctx, agent, home) = await CreateAgent();
        AppendTurn(agent);

        using var chat = new ChatWindow(ctx, agent, home);
        chat.DrainUi();

        var frame = DrawFrame(chat);
        Assert.Equal(1, Count(frame, "你好世界"));
        Assert.Equal(1, Count(frame, "流式回答甲"));
        Assert.Equal(1, Count(frame, "⚙ bash"));
        Assert.Equal(1, Count(frame, "工具输出乙"));
    }

    [Fact]
    public async Task Resume_Switches_And_Replays_Without_Duplicates()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        AppendTurn(agent, "继续任务丙", "回答丁");
        chat.DrainUi();

        Type(chat, "/new");
        Press(chat, ConsoleKey.Enter);
        var frame = await PumpUntilAsync(chat, text => text.Contains("new session:", StringComparison.Ordinal));
        Assert.Contains("new session:", frame);
        Assert.DoesNotContain("回答丁", frame);

        Type(chat, $"/resume {agent.Id.Value}");
        Press(chat, ConsoleKey.Enter);
        frame = await PumpUntilAsync(chat, text => text.Contains("resumed:", StringComparison.Ordinal));
        Assert.Contains("resumed:", frame);
        Assert.Equal(1, Count(frame, "继续任务丙"));
        Assert.Equal(1, Count(frame, "回答丁"));
        Assert.Equal(1, Count(frame, "工具输出乙"));
    }

    [Fact]
    public async Task AssistantMessage_Without_Streamed_Chunks_Renders_Final_Text()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        agent.Session.Append(new TurnStartPayload(1));
        agent.Session.Append(
            new AssistantMessagePayload(1, 1, MessageFactory.CreateAssistantMessage([new TextBlock("非流式回答戊")], "p", "m")),
            new SurfaceOp.Append());
        agent.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        chat.DrainUi();

        Assert.Equal(1, Count(DrawFrame(chat), "非流式回答戊"));
    }

    [Fact]
    public async Task Streamed_Chunks_Then_Final_Message_Not_Duplicated()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        agent.Session.Append(new TurnStartPayload(1));
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "流式回答甲")));
        agent.Session.Append(
            new AssistantMessagePayload(1, 1, MessageFactory.CreateAssistantMessage([new TextBlock("流式回答甲")], "p", "m")),
            new SurfaceOp.Append());
        agent.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        chat.DrainUi();

        Assert.Equal(1, Count(DrawFrame(chat), "流式回答甲"));
    }

    [Fact]
    public async Task Redelivered_Event_Is_Skipped()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        agent.Session.Append(new TurnStartPayload(1));
        var appended = agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "唯一片段")));
        chat.DrainUi();

        ctx.Events.Emit(ctx, new SessionEventNotification(agent.Session, appended));
        chat.DrainUi();

        Assert.Equal(1, Count(DrawFrame(chat), "唯一片段"));
    }

    [Fact]
    public async Task Approval_Prompt_Shows_Primary_Argument_And_Impact()
    {
        var (ctx, agent, home) = await CreateAgent();
        var approval = ApprovalService.Register(ctx);
        using var chat = new ChatWindow(ctx, agent, home);
        agent.Session.Append(new TurnStartPayload(1));
        chat.DrainUi();

        var request = new ApprovalRequest(agent, "bash", ToolCallId.Create("call-1"), null, """{"command":"rm -rf ./build"}""");
        var outcomeTask = approval.Request(request, CancellationToken.None);
        chat.DrainUi();

        var frame = DrawFrame(chat);
        Assert.Contains("rm -rf ./build", frame);
        Assert.Contains("可能删除文件", frame);

        Press(chat, ConsoleKey.Y);
        Assert.Equal(ApprovalOutcome.AllowedOnce, await outcomeTask);
    }

    [Fact]
    public async Task AskUser_SingleSelect_Answers_With_Selected_Option()
    {
        var (ctx, agent, home) = await CreateAgent();
        var userQuestions = UserQuestionService.Register(ctx);
        using var chat = new ChatWindow(ctx, agent, home);
        var question = new AskUserQuestionItem(
            "q1",
            "选择策略",
            Options: [new AskUserQuestionOption("甲"), new AskUserQuestionOption("乙")]);

        var answerTask = userQuestions.Ask(new AskUserQuestionRequest([question], agent), TestContext.Current.CancellationToken);
        chat.DrainUi();
        var frame = DrawFrame(chat);
        Assert.Contains("选择策略", frame);
        Assert.Contains("[2] 乙", frame);

        Type(chat, "2");
        Press(chat, ConsoleKey.Enter);

        var item = Assert.Single((await answerTask).Answers);
        Assert.Equal("q1", item.Id);
        Assert.Equal("乙", Assert.Single(item.Selected));
        Assert.Null(item.Custom);
    }

    [Fact]
    public async Task AskUser_MultiSelect_Toggles_And_Keeps_Both()
    {
        var (ctx, agent, home) = await CreateAgent();
        var userQuestions = UserQuestionService.Register(ctx);
        using var chat = new ChatWindow(ctx, agent, home);
        var question = new AskUserQuestionItem(
            "q1",
            "选多项",
            Options: [new AskUserQuestionOption("甲"), new AskUserQuestionOption("乙"), new AskUserQuestionOption("丙")],
            MultiSelect: true);

        var answerTask = userQuestions.Ask(new AskUserQuestionRequest([question], agent), TestContext.Current.CancellationToken);
        chat.DrainUi();

        Type(chat, "1");
        Type(chat, "3");
        Press(chat, ConsoleKey.Enter);

        var item = Assert.Single((await answerTask).Answers);
        Assert.Equal(["甲", "丙"], item.Selected);
    }

    [Fact]
    public async Task AskUser_FreeText_And_MultiQuestion_Flow()
    {
        var (ctx, agent, home) = await CreateAgent();
        var userQuestions = UserQuestionService.Register(ctx);
        using var chat = new ChatWindow(ctx, agent, home);
        var first = new AskUserQuestionItem(
            "q1",
            "选一个方向",
            Options: [new AskUserQuestionOption("保守"), new AskUserQuestionOption("激进")]);
        var second = new AskUserQuestionItem("q2", "补充说明");

        var answerTask = userQuestions.Ask(new AskUserQuestionRequest([first, second], agent), TestContext.Current.CancellationToken);
        chat.DrainUi();

        Type(chat, "2");
        Press(chat, ConsoleKey.Enter);
        var frame = DrawFrame(chat);
        Assert.Contains("补充说明", frame);

        Type(chat, "按方案二来");
        Press(chat, ConsoleKey.Enter);

        var answer = await answerTask;
        Assert.Equal(2, answer.Answers.Count);
        Assert.Equal("激进", Assert.Single(answer.Answers[0].Selected));
        Assert.Equal("按方案二来", answer.Answers[1].Custom);
    }

    [Fact]
    public async Task AskUser_Enter_Without_Answer_Stays_Pending()
    {
        var (ctx, agent, home) = await CreateAgent();
        var userQuestions = UserQuestionService.Register(ctx);
        using var chat = new ChatWindow(ctx, agent, home);
        var question = new AskUserQuestionItem(
            "q1",
            "选择策略",
            Options: [new AskUserQuestionOption("甲"), new AskUserQuestionOption("乙")]);

        var answerTask = userQuestions.Ask(new AskUserQuestionRequest([question], agent), TestContext.Current.CancellationToken);
        chat.DrainUi();

        Press(chat, ConsoleKey.Enter);
        Assert.False(answerTask.IsCompleted);
        Assert.Contains("请先选择", DrawFrame(chat));

        Type(chat, "1");
        Press(chat, ConsoleKey.Enter);
        Assert.Equal("甲", Assert.Single(Assert.Single((await answerTask).Answers).Selected));
    }

    [Fact]
    public async Task AskUser_Escape_Cancels_With_NoProvider()
    {
        var (ctx, agent, home) = await CreateAgent();
        var userQuestions = UserQuestionService.Register(ctx);
        using var chat = new ChatWindow(ctx, agent, home);
        var question = new AskUserQuestionItem("q1", "选择策略", Options: [new AskUserQuestionOption("甲")]);

        var answerTask = userQuestions.Ask(new AskUserQuestionRequest([question], agent), TestContext.Current.CancellationToken);
        chat.DrainUi();
        Press(chat, ConsoleKey.Escape);

        var error = await Assert.ThrowsAsync<UserQuestionException>(async () => await answerTask);
        Assert.Equal(UserQuestionException.NoProvider, error.Code);
    }

    private async Task<(Context Ctx, AgentLoopAgent Agent, HarnessHome Home)> CreateAgent()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        CommandsService.Register(ctx);
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("deepseek-official", "deepseek-v4-flash")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        return (ctx, agent, HarnessHome.Resolve(_homeDir));
    }

    private static void AppendTurn(AgentLoopAgent agent, string userText = "你好世界", string assistantText = "流式回答甲")
    {
        agent.Session.Append(new TurnStartPayload(1));
        agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText(userText)), new SurfaceOp.Append());
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, assistantText)));
        agent.Session.Append(
            new AssistantMessagePayload(1, 1, MessageFactory.CreateAssistantMessage([new TextBlock(assistantText)], "p", "m")),
            new SurfaceOp.Append());
        var callId = ToolCallId.Create("call-1");
        var callSeq = agent.Session.Seq;
        agent.Session.Append(new ToolCallPayload(1, 1, callId, "bash", """{"command":"ls"}"""));
        agent.Session.Append(
            new ToolResultPayload(1, 1, MessageFactory.CreateToolResultMessage(callId, [new TextBlock("工具输出乙")], false)),
            new SurfaceOp.Append(),
            [callSeq]);
        agent.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
    }

    private static void Type(ChatWindow chat, string text)
    {
        foreach (var character in text)
            chat.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false));
    }

    private static void Press(ChatWindow chat, ConsoleKey key)
        => chat.HandleKey(new ConsoleKeyInfo('\0', key, false, false, false));

    private static async Task<string> PumpUntilAsync(ChatWindow chat, Func<string, bool> predicate)
    {
        var frame = "";
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            chat.DrainUi();
            frame = DrawFrame(chat);
            if (predicate(frame))
                return frame;
            await Task.Delay(5);
        }
        return frame;
    }

    private static string DrawFrame(ChatWindow chat)
    {
        var layout = LayoutEngine.Calculate(120, 40);
        var grid = new CellGrid(120, 40);
        chat.Draw(grid, layout);
        var lines = new List<string>();
        for (var y = 0; y < grid.Height; y++)
        {
            var chars = new char[grid.Width];
            for (var x = 0; x < grid.Width; x++)
                chars[x] = grid[x, y].Character;
            lines.Add(new string(chars).Replace("\0", ""));
        }

        return string.Join('\n', lines);
    }

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    public void Dispose()
    {
        if (Directory.Exists(_homeDir))
            Directory.Delete(_homeDir, true);
    }
}
