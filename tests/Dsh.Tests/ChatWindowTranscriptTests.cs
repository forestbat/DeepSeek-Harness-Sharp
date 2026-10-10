using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Presets;
using Dsh.Runtime;
using Dsh.Tui;
using Dsh.Tui.Services;

namespace Dsh.Tests;

/** TUI 会话流渲染: 历史回放、重复渲染守卫、非流式补渲染、审批提示参数。 */
[Collection("RenderBench")] // 分隔线出图用 GL, 与其它 GL 用例串行
public sealed class ChatWindowTranscriptTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")),
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

    [Fact]
    public async Task Input_Info_Line_Shows_Current_Preset()
    {
        var (ctx, agent, home) = await CreateAgent();
        PresetModePayload.RegisterCodec();
        using var chat = new ChatWindow(ctx, agent, home);
        chat.DrainUi();

        Assert.Contains("standard - deepseek-official - deepseek-v4-flash", DrawFrame(chat));

        agent.Session.Append(new PresetModePayload(InteractionPreset.Minimal));
        chat.DrainUi();

        Assert.Contains("minimal - deepseek-official - deepseek-v4-flash", DrawFrame(chat));
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

    /** 鼠标点击 fold 头行切换折叠, 与键盘 Tab/Enter 走同一入口(阶段 3.1)。 */
    [Fact]
    public async Task MouseClick_On_CodeFence_FoldRow_Toggles_Expansion()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        agent.Session.Append(new TurnStartPayload(1));
        var text = "前言\n```\n代码甲\n代码乙\n```\n后语";
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, text)));
        agent.Session.Append(
            new AssistantMessagePayload(1, 1, MessageFactory.CreateAssistantMessage([new TextBlock(text)], "p", "m")),
            new SurfaceOp.Append());
        agent.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        chat.DrainUi();

        var layout = LayoutEngine.Calculate(120, 40);
        var collapsed = DrawRows(chat, layout);
        Assert.DoesNotContain(collapsed, row => row.Contains("代码甲", StringComparison.Ordinal));
        var foldRow = collapsed.FindIndex(row => row.Contains("```", StringComparison.Ordinal));
        Assert.True(foldRow >= 0, "折叠态应有 ``` 预览行");

        chat.HandleMouseClick(2, foldRow, layout);
        var expanded = DrawRows(chat, layout);
        Assert.Contains(expanded, row => row.Contains("代码甲", StringComparison.Ordinal));

        Press(chat, ConsoleKey.Tab);
        Press(chat, ConsoleKey.Enter);
        var recollapsed = DrawRows(chat, layout);
        Assert.DoesNotContain(recollapsed, row => row.Contains("代码甲", StringComparison.Ordinal));
    }

    /** 拖动选择: 选中文本被提取并高亮(阶段 3.2)。 */
    [Fact]
    public async Task MouseDrag_SelectsTranscriptText_AndHighlightsIt()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        agent.Session.Append(new TurnStartPayload(1));
        const string text = "第一行内容\n第二行内容";
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, text)));
        agent.Session.Append(
            new AssistantMessagePayload(1, 1, MessageFactory.CreateAssistantMessage([new TextBlock(text)], "p", "m")),
            new SurfaceOp.Append());
        agent.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        chat.DrainUi();

        var layout = LayoutEngine.Calculate(120, 40);
        var grid = new CellGrid(120, 40);
        chat.Draw(grid, layout);
        var rows = Rows(grid);
        var first = rows.FindIndex(row => row.Contains("第一行内容", StringComparison.Ordinal));
        var second = rows.FindIndex(row => row.Contains("第二行内容", StringComparison.Ordinal));
        Assert.True(first >= 0 && second > first, "两行正文都应渲染");

        chat.HandleMouseClick(0, first, layout);
        chat.HandleMouseDrag(40, second, layout);
        var highlighted = new CellGrid(120, 40);
        chat.Draw(highlighted, layout);
        Assert.Contains(
            Enumerable.Range(0, 10),
            column => (highlighted[column, second].Style & CellStyle.Reverse) != 0);

        var selected = chat.HandleMouseRelease(40, second, layout);
        Assert.Equal("第一行内容\n第二行内容", selected);
    }

    /** 双 Ctrl+C 退出: 键盘事件(C+Control)直投 HandleKey, 不依赖平台输入源。 */
    [Fact]
    public async Task DoubleCtrlC_RequestsExit()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        chat.DrainUi();

        chat.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.C, false, false, true));
        Assert.False(chat.ExitRequested);

        chat.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.C, false, false, true));
        Assert.True(chat.ExitRequested);
    }

    /** TUI diff 卡片配色(阶段 4 补齐"红删绿增"): 增行绿底、删行红底、标题青色加粗。 */
    [Fact]
    public async Task TuiDiffCard_RendersGreenAddsRedDeletesAndTitle()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        var callId = ToolCallId.Create("call-diff");
        agent.Session.Append(new TurnStartPayload(1));
        var callSeq = agent.Session.Seq;
        agent.Session.Append(new ToolCallPayload(
            1, 1, callId, "str_replace_editor", """{"command":"str_replace","path":"src/sample.cs"}"""));
        using var meta = System.Text.Json.JsonDocument.Parse(
            """{"card":"diff","title":"edit src/sample.cs","diffs":[{"path":"src/sample.cs","oldText":"第一行\n第二行\n","newText":"第一行\n改过的第二行\n新增行\n"}]}""");
        agent.Session.Append(
            new ToolResultPayload(
                1,
                1,
                MessageFactory.CreateToolResultMessage(callId, [new TextBlock("applied")], false),
                null,
                meta.RootElement.Clone()),
            new SurfaceOp.Append(),
            [callSeq]);
        agent.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        chat.DrainUi();

        var layout = LayoutEngine.Calculate(120, 40);
        var grid = new CellGrid(120, 40);
        chat.Draw(grid, layout);
        var folded = Rows(grid);
        Assert.Contains(folded, row => row.Contains("edit src/sample.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(folded, row => row.Contains("改过的第二行", StringComparison.Ordinal));

        // diff 卡片默认折叠: Tab 选中 + Enter 展开后再断言配色
        Press(chat, ConsoleKey.Tab);
        Press(chat, ConsoleKey.Enter);
        var expanded = new CellGrid(120, 40);
        chat.Draw(expanded, layout);
        var rows = Rows(expanded);
        var title = rows.FindIndex(row => row.Contains("edit src/sample.cs", StringComparison.Ordinal));
        var added = rows.FindIndex(row => row.Contains("改过的第二行", StringComparison.Ordinal));
        var removed = rows.FindIndex(row =>
            row.Contains("第二行", StringComparison.Ordinal) && !row.Contains("改过的", StringComparison.Ordinal));
        var inserted = rows.FindIndex(row => row.Contains("新增行", StringComparison.Ordinal));
        Assert.True(
            title >= 0 && added >= 0 && removed >= 0 && inserted >= 0,
            "diff 卡片各行都应渲染\n" + string.Join('\n', rows.Where(row => row.Trim().Length > 0)));
        Assert.Equal(AnsiColor.Green, expanded[0, added].Background);
        Assert.Equal(AnsiColor.Green, expanded[0, inserted].Background);
        Assert.Equal(AnsiColor.Red, expanded[0, removed].Background);
        Assert.Equal(AnsiColor.BrightCyan, expanded[0, title].Foreground);
        Assert.True((expanded[0, title].Style & CellStyle.Bold) != 0);
    }

    /** 鼠标拖动分隔线调整窗格比例: 分隔线跟手, 且两侧不小于最小格数。需要真实桌面渲染环境(EGL)。 */
    [Fact]
    [Trait("Category", "OnlyGpu")]
    public async Task MouseDrag_OnDivider_ResizesPanes()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        chat.DrainUi();
        var layout = LayoutEngine.Calculate(120, 40);

        chat.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.X, false, false, true));
        chat.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.OemPlus, false, false, false));
        chat.DrainUi();
        var divider = FindPaneDividerColumn(chat, layout);
        Assert.True(divider > 0, "分屏后应有分隔线");

        chat.HandleMouseClick(divider, 1, layout);
        chat.HandleMouseDrag(divider + 6, 1, layout);
        chat.HandleMouseRelease(divider + 6, 1, layout);
        Assert.Equal(divider + 6, FindPaneDividerColumn(chat, layout));

        // 拖拽后的屏幕出图(工程自身渲染管线)
        var captured = new CellGrid(120, 40);
        chat.Draw(captured, layout);
        var screen = new char[120, 40];
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 120; x++)
            {
                var character = captured[x, y].Character;
                screen[x, y] = character == '\0' ? ' ' : character;
            }
        }

        TuiScreenshot.SavePng(screen, $"tui-screen-split-{(OperatingSystem.IsWindows() ? "windows" : "linux")}.png");

        // 拖到最左: 左侧窗格至少保留 MinimumPaneExtent 格
        chat.HandleMouseClick(divider + 6, 1, layout);
        chat.HandleMouseDrag(0, 1, layout);
        chat.HandleMouseRelease(0, 1, layout);
        Assert.Equal(LayoutEngine.MinimumPaneExtent, FindPaneDividerColumn(chat, layout));
    }

    /** 在窗格区域内找竖直分隔线的屏幕列(右侧固定面板的分隔线不在 Main 内, 天然排除)。 */
    /** 侧栏宽度要跨进程沿用: 拖完写进 settings.yaml, 新起的 ChatWindow 直接量出拖出来的宽度。 */
    [Fact]
    public async Task RightPanelWidth_Persists_Across_ChatWindows()
    {
        var (ctx, agent, home) = await CreateAgent();
        var layout = LayoutEngine.Calculate(120, 40);
        var settings = new TuiSettings(home);
        settings.SidebarWidth = 30;

        using (var chat = new ChatWindow(ctx, agent, home, null, settings))
        {
            chat.DrainUi();
            var grid = new CellGrid(120, 40);
            chat.Draw(grid, layout);
            Assert.Equal('│', grid[120 - 1 - 30, 1].Character);
        }

        using (var chat = new ChatWindow(ctx, agent, home, null, settings))
        {
            chat.DrainUi();
            var divider = 120 - 1 - 30;
            chat.HandleMouseClick(divider, 1, layout);
            chat.HandleMouseDrag(divider + 8, 1, layout);
            chat.HandleMouseRelease(divider + 8, 1, layout);
        }

        Assert.Equal(22, settings.SidebarWidth);

        using (var chat = new ChatWindow(ctx, agent, home, null, new TuiSettings(home)))
        {
            chat.DrainUi();
            var grid = new CellGrid(120, 40);
            chat.Draw(grid, layout);
            Assert.Equal('│', grid[120 - 1 - 22, 1].Character);
        }
    }

    /** 输入栏上沿可用鼠标上下拖动改高度: 拖完输入行变高(含信息行), 状态行仍在最后一行。 */
    [Fact]
    public async Task MouseDrag_OnInputDivider_ResizesInputBar()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        chat.DrainUi();
        var layout = LayoutEngine.Calculate(120, 40);
        Assert.Equal(LayoutEngine.InputHeight, layout.Input.Height);

        var dividerRow = layout.Input.Y - 1;
        chat.HandleMouseClick(10, dividerRow, layout);
        chat.HandleMouseDrag(10, dividerRow - 4, layout);
        chat.HandleMouseRelease(10, dividerRow - 4, layout);

        var grid = new CellGrid(120, 40);
        chat.Draw(grid, layout);
        var grown = LayoutEngine.Calculate(120, 40, null, 6);
        Assert.Equal(6, grown.Input.Height);

        var rows = Rows(grid);
        // 输入行与说明行锚在输入区底部: 多出来的空行留在上方, 说明行紧贴状态行(layout.Input.Bottom - 1)。
        Assert.Contains("Enter 发送", rows[grown.Input.Bottom - 1]);
        Assert.DoesNotContain("Enter 发送", rows[grown.Input.Y + 1]);
        Assert.Equal(40, rows.Count);
    }

    /** 输入栏高度要跨进程沿用: 写进 settings.yaml 后, 新起的 ChatWindow 直接量出拖出来的高度。 */
    [Fact]
    public async Task InputHeight_Persists_Across_ChatWindows()
    {
        var (ctx, agent, home) = await CreateAgent();
        var settings = new TuiSettings(home);
        settings.InputHeight = 6;

        using var chat = new ChatWindow(ctx, agent, home, null, settings);
        chat.DrainUi();
        var grid = new CellGrid(120, 40);
        chat.Draw(grid, LayoutEngine.Calculate(120, 40));

        var grown = LayoutEngine.Calculate(120, 40, null, 6);
        Assert.Contains("Enter 发送", Rows(grid)[grown.Input.Bottom - 1]);
    }

    /** 右栏内容超屏时可用滚轮滚动(窄终端里才看得到下面的段落), 偏移从顶部起算。 */
    [Fact]
    public async Task MouseWheel_OnRightPanel_ScrollsItsContent()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        chat.DrainUi();
        var layout = LayoutEngine.Calculate(120, 16);

        var before = new CellGrid(120, 16);
        chat.Draw(before, layout);
        Assert.Contains("上下文", string.Join('\n', Rows(before)));
        Assert.Equal(0, chat.RightPanelScrollOffset);

        for (var step = 0; step < 5; step++)
            chat.HandleMouseWheel(-3, layout.RightPanel.X + 2, 1, layout);

        var after = new CellGrid(120, 16);
        chat.Draw(after, layout);
        Assert.True(chat.RightPanelScrollOffset > 0);
        Assert.DoesNotContain("上下文", string.Join('\n', Rows(after)));
    }

    private static int FindPaneDividerColumn(ChatWindow chat, UiLayout layout)
    {
        var grid = new CellGrid(120, 40);
        chat.Draw(grid, layout);
        for (var y = 0; y < layout.Main.Bottom; y++)
        {
            for (var x = 1; x < layout.Main.Width; x++)
            {
                if (grid[x, y].Character == '│')
                    return x;
            }
        }
        return -1;
    }

    /** 单窗格(默认布局)的右栏分割线也要能用鼠标拖动: 拖动后竖线跟手, 且左侧正文不小于最小宽度。 */
    [Fact]
    public async Task MouseDrag_OnRightPanelDivider_ResizesSidebar()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        chat.DrainUi();
        var layout = LayoutEngine.Calculate(120, 40);
        var divider = layout.Main.X + layout.Main.Width;

        var before = new CellGrid(120, 40);
        chat.Draw(before, layout);
        Assert.Equal('│', before[divider, 1].Character);

        chat.HandleMouseClick(divider, 1, layout);
        chat.HandleMouseDrag(divider - 10, 1, layout);
        chat.HandleMouseRelease(divider - 10, 1, layout);

        var after = new CellGrid(120, 40);
        chat.Draw(after, layout);
        Assert.Equal('│', after[divider - 10, 1].Character);
        Assert.NotEqual('│', after[divider, 1].Character);

        // 拖到最右: 右栏收缩到最小宽度 10, 分割线仍在(不会把侧栏拖没了导致再也拖不回来)。
        chat.HandleMouseClick(divider - 10, 1, layout);
        chat.HandleMouseDrag(119, 1, layout);
        chat.HandleMouseRelease(119, 1, layout);

        var clamped = new CellGrid(120, 40);
        chat.Draw(clamped, layout);
        Assert.Equal('│', clamped[120 - 1 - 10, 1].Character);
    }

    private static List<string> Rows(CellGrid grid)
    {
        var lines = new List<string>();
        for (var y = 0; y < grid.Height; y++)
        {
            var chars = new char[grid.Width];
            for (var x = 0; x < grid.Width; x++)
                chars[x] = grid[x, y].Character;
            lines.Add(new string(chars).Replace("\0", ""));
        }
        return lines;
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
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        return frame;
    }

    private static string DrawFrame(ChatWindow chat)
        => string.Join('\n', DrawRows(chat, LayoutEngine.Calculate(120, 40)));

    private static List<string> DrawRows(ChatWindow chat, UiLayout layout)
    {
        var grid = new CellGrid(120, 40);
        chat.Draw(grid, layout);
        return Rows(grid);
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

    public void Dispose() => TempTree.Delete(_homeDir);
}
