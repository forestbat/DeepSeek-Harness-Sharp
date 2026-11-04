using System.Text.Json;
using System.Text;
using Dsh.Interaction.AskUser;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tui;
using Dsh.Tui.Services;

namespace Dsh.Tests;

[Collection("TuiTheme")]
public sealed class ChoicePromptTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")), Guid.NewGuid().ToString("N"));

    [Fact]
    public void Defaults_DisabledAndValues_AreSharedByQuestionConsumers()
    {
        var state = new ChoicePromptState(ChoicePrompt.FromQuestion(new("q", "Choose", Options:
        [new("Disabled", Value: "disabled", Disabled: true, Default: true),
         new("First", Value: "first", Default: true),
         new("Second", Value: "second", Default: true)])));
        Assert.Equal(1, state.FocusedIndex);
        Assert.Equal(["first"], state.SelectedValues());
        state.Select(0);
        Assert.Equal(["first"], state.SelectedValues());
        state.Move(1);
        Assert.Equal(2, state.FocusedIndex);
        state.Move(1);
        Assert.Equal(2, state.FocusedIndex);
        state.Select(2);
        Assert.Equal(["second"], state.BuildAnswer("q").Selected);
        state.Home();
        Assert.Equal(1, state.FocusedIndex);
        state.End();
        Assert.Equal(2, state.FocusedIndex);
        state.Move(0);
        Assert.Equal(2, state.FocusedIndex);
    }

    [Fact]
    public void AllDisabledAndCustomForbidden_CannotSubmitOrSmuggleCustomText()
    {
        var control = new ChoicePromptControl(new("Unavailable", null, [new("Disabled", null, "x", Disabled: true, Default: true)], AllowCustom: false));
        bool? dismissed = null;
        control.Completed += value => dismissed = value;
        control.InsertText("not allowed");
        control.HandleKey(Key(ConsoleKey.D1, '1'));
        control.HandleKey(Key(ConsoleKey.Spacebar));
        control.HandleKey(Key(ConsoleKey.Home));
        control.HandleKey(Key(ConsoleKey.End));
        control.HandleKey(Key(ConsoleKey.Enter));
        Assert.False(control.State.CanSubmit);
        Assert.Empty(control.State.CustomText);
        Assert.Empty(control.State.SelectedValues());
        Assert.Equal(-1, control.State.FocusedIndex);
        Assert.Null(dismissed);
        control.HandleKey(Key(ConsoleKey.Escape));
        Assert.True(dismissed);
    }

    [Fact]
    public void EmptyOptions_CustomOnlyQuestion_OpensInEditableFocus()
    {
        var control = new ChoicePromptControl(new("Explain", null, []));
        bool? dismissed = null;
        control.Completed += value => dismissed = value;
        control.HandleKey(Key(ConsoleKey.NoName, '答'));
        control.HandleKey(Key(ConsoleKey.Enter));
        Assert.False(dismissed);
        Assert.Equal("答", control.State.BuildAnswer("q").Custom);
        Assert.Empty(control.State.SelectedValues());
    }

    [Fact]
    public void Keyboard_MultiSelectNumbersSpaceTabAndCustomCursor_ProduceOneAnswer()
    {
        var control = new ChoicePromptControl(new("Choose", null,
        [new("A", null, "a", Default: true), new("Disabled", null, "disabled", Disabled: true), new("C", null, "c")], MultiSelect: true));
        bool? dismissed = null;
        control.Completed += value => dismissed = value;
        control.HandleKey(Key(ConsoleKey.D2, '2'));
        control.HandleKey(Key(ConsoleKey.DownArrow));
        Assert.Equal(2, control.State.FocusedIndex);
        control.HandleKey(Key(ConsoleKey.Spacebar, ' '));
        control.HandleKey(Key(ConsoleKey.Tab));
        control.InsertText("中文abc");
        control.HandleKey(Key(ConsoleKey.LeftArrow));
        control.HandleKey(Key(ConsoleKey.Backspace));
        control.HandleKey(Key(ConsoleKey.NoName, 'X'));
        Assert.Equal("中文aXc", control.State.CustomText);
        control.HandleKey(Key(ConsoleKey.Enter));
        Assert.False(dismissed);
        var answer = control.State.BuildAnswer("q");
        Assert.Equal(["a", "c"], answer.Selected);
        Assert.Equal("中文aXc", answer.Custom);
        control.HandleKey(Key(ConsoleKey.Escape));
        Assert.False(dismissed);
    }

    [Fact]
    public void Keyboard_HomeEndAndReverseTab_ReachCancelWithoutSelectingDisabled()
    {
        var control = new ChoicePromptControl(new("Choose", null,
        [new("Disabled", null, "x", Disabled: true), new("A", null, "a"), new("B", null, "b")], AllowCustom: false));
        bool? dismissed = null;
        control.Completed += value => dismissed = value;
        control.HandleKey(Key(ConsoleKey.End));
        Assert.Equal(2, control.State.FocusedIndex);
        control.HandleKey(Key(ConsoleKey.Home));
        Assert.Equal(1, control.State.FocusedIndex);
        control.HandleKey(new('\t', ConsoleKey.Tab, true, false, false));
        control.HandleKey(Key(ConsoleKey.Enter));
        Assert.True(dismissed);
        Assert.Empty(control.State.SelectedValues());
    }

    [Fact]
    public void CjkWrappedContinuation_MouseHoverAndClick_HitTheRenderedOptionOnly()
    {
        var control = new ChoicePromptControl(new("Pick", null,
        [new("第一选项", "换行后的中文说明", "first"), new("第二选项", null, "second")], MultiSelect: true));
        var grid = new CellGrid(20, 14);
        var bounds = new ConsoleRect(2, 1, 12, 12);
        control.Draw(grid, bounds);
        control.HandlePointer(3, 4, click: false);
        Assert.Equal(0, control.State.FocusedIndex);
        Assert.Empty(control.State.SelectedValues());
        control.HandlePointer(3, 4, click: true);
        Assert.Equal(["first"], control.State.SelectedValues());
        bool? dismissed = null;
        control.Completed += value => dismissed = value;
        control.HandlePointer(0, 4, click: true);
        control.HandlePointer(bounds.Right - 1, bounds.Bottom - 2, click: true);
        Assert.Null(dismissed);
        control.HandlePointer(bounds.X + 1, bounds.Bottom - 2, click: true);
        Assert.False(dismissed);
    }

    [Fact]
    public void LongContent_ScrollClamps_AndEndKeepsFocusedOptionVisible()
    {
        var control = new ChoicePromptControl(new("Pick", new string('d', 240),
            [.. Enumerable.Range(0, 20).Select(index => new ChoicePromptOption($"Option {index}", "中文说明", index.ToString()))]));
        var grid = new CellGrid(30, 12);
        control.Draw(grid, new(0, 0, 30, 12));
        control.Scroll(int.MaxValue);
        var bottom = control.ScrollOffset;
        Assert.True(bottom > 0);
        control.Scroll(1);
        Assert.Equal(bottom, control.ScrollOffset);
        control.Scroll(-int.MaxValue);
        Assert.Equal(0, control.ScrollOffset);
        control.HandleKey(Key(ConsoleKey.End));
        control.Draw(grid, new(0, 0, 30, 12));
        Assert.Equal(19, control.State.FocusedIndex);
        Assert.True(control.ScrollOffset > 0);
        control.HandlePointer(1, 8, click: true);
        Assert.Equal(["19"], control.State.SelectedValues());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(6, 4)]
    public void TinyViewport_DoesNotWriteOutsideItsGrid(int width, int height)
    {
        var control = new ChoicePromptControl(new("中文标题", "长说明", [new("中文选项", null, "value")]));
        control.Draw(new CellGrid(width, height), new(0, 0, width, height));
        control.Scroll(1);
        control.HandlePointer(width, height, click: true);
        Assert.Empty(control.State.SelectedValues());
    }

    [Fact]
    public async Task QuestionGroup_EscapeDiscardsPriorAnswers_AndRestoresDraftAndCursor()
    {
        using var chat = await CreateChat();
        var pane = chat.InputPane;
        pane.Input = "original 中文 draft";
        pane.Cursor = 5;
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.ShowQuestionPrompt(new([new("first", "First", Options: [new("A")]), new("second", "Second", Options: [new("B")])]), pending);
        chat.HandleKey(Key(ConsoleKey.Enter));
        Assert.False(pending.Task.IsCompleted);
        chat.HandleKey(Key(ConsoleKey.Escape));
        var result = Assert.IsType<AskUserQuestionAnswer>(await pending.Task);
        Assert.Equal("dismissed", result.Status);
        Assert.Equal("user_cancelled", result.Reason);
        Assert.Empty(result.Answers);
        Assert.Equal("original 中文 draft", pane.Input);
        Assert.Equal(5, pane.Cursor);
        Assert.False(pane.HasChoicePrompt);
        Assert.False(chat.ExitRequested);
    }

    [Theory]
    [InlineData(ConsoleKey.Y, ApprovalOutcome.AllowedOnce)]
    [InlineData(ConsoleKey.A, ApprovalOutcome.AllowedForSession)]
    [InlineData(ConsoleKey.N, ApprovalOutcome.Rejected)]
    [InlineData(ConsoleKey.C, ApprovalOutcome.Cancelled)]
    [InlineData(ConsoleKey.Escape, ApprovalOutcome.Cancelled)]
    public async Task Approval_ShortcutsAndEscape_ReturnExactOutcomeWithoutExiting(ConsoleKey key, ApprovalOutcome expected)
    {
        using var chat = await CreateChat();
        var pane = chat.InputPane;
        pane.Input = "saved draft";
        pane.Cursor = 3;
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.ShowApprovalPrompt(new(pane.Agent, "bash", ToolCallId.Create("call-choice"), "Run a command", "{\"command\":\"echo hello\"}"), pending);
        chat.HandleKey(Key(key));
        Assert.Equal(expected, Assert.IsType<ApprovalOutcome>(await pending.Task));
        Assert.False(chat.ExitRequested);
        Assert.Equal("saved draft", pane.Input);
        Assert.Equal(3, pane.Cursor);
    }

    [Theory]
    [InlineData(ConsoleKey.A, ConsoleModifiers.Control)]
    [InlineData(ConsoleKey.Y, ConsoleModifiers.Alt)]
    [InlineData(ConsoleKey.N, ConsoleModifiers.Control | ConsoleModifiers.Alt)]
    [InlineData(ConsoleKey.C, ConsoleModifiers.Control)]
    public async Task Approval_ModifiedLetters_DoNotMakeADecision(ConsoleKey key, ConsoleModifiers modifiers)
    {
        using var chat = await CreateChat();
        var pane = chat.InputPane;
        pane.Input = "saved draft";
        pane.Cursor = 3;
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.ShowApprovalPrompt(new(pane.Agent, "bash", ToolCallId.Create("modified-choice"), "Run a command"), pending);
        chat.HandleKey(new('\0', key, false, (modifiers & ConsoleModifiers.Alt) != 0, (modifiers & ConsoleModifiers.Control) != 0));
        Assert.False(pending.Task.IsCompleted);
        Assert.True(pane.HasChoicePrompt);
        Assert.False(chat.ExitRequested);
        chat.HandleKey(Key(ConsoleKey.Escape));
        Assert.Equal(ApprovalOutcome.Cancelled, Assert.IsType<ApprovalOutcome>(await pending.Task));
        Assert.Equal("saved draft", pane.Input);
        Assert.Equal(3, pane.Cursor);
    }

    [Theory]
    [InlineData(ConsoleModifiers.Control)]
    [InlineData(ConsoleModifiers.Alt)]
    public async Task Approval_ModifiedEnter_DoesNotSubmitTheFocusedChoice(ConsoleModifiers modifiers)
    {
        using var chat = await CreateChat();
        var pane = chat.InputPane;
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.ShowApprovalPrompt(new(pane.Agent, "bash", ToolCallId.Create("modified-enter"), "Run a command"), pending);
        chat.HandleKey(new('\r', ConsoleKey.Enter, false, (modifiers & ConsoleModifiers.Alt) != 0, (modifiers & ConsoleModifiers.Control) != 0));
        Assert.False(pending.Task.IsCompleted);
        Assert.True(pane.HasChoicePrompt);
        chat.HandleKey(Key(ConsoleKey.Escape));
        Assert.Equal(ApprovalOutcome.Cancelled, Assert.IsType<ApprovalOutcome>(await pending.Task));
    }

    [Theory]
    [InlineData("y", ApprovalOutcome.AllowedOnce)]
    [InlineData("Y", ApprovalOutcome.AllowedOnce)]
    [InlineData("a", ApprovalOutcome.AllowedForSession)]
    [InlineData("A", ApprovalOutcome.AllowedForSession)]
    [InlineData("n", ApprovalOutcome.Rejected)]
    [InlineData("N", ApprovalOutcome.Rejected)]
    [InlineData("c", ApprovalOutcome.Cancelled)]
    [InlineData("C", ApprovalOutcome.Cancelled)]
    public async Task Approval_RealUnixCharacterInput_SelectsTheAdvertisedAction(string text, ApprovalOutcome expected)
    {
        using var chat = await CreateChat();
        var pane = chat.InputPane;
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.ShowApprovalPrompt(new(pane.Agent, "bash", ToolCallId.Create("unix-approval"), "Run a command"), pending);
        var parser = new TerminalInputParser();
        parser.Append(Encoding.UTF8.GetBytes(text));
        Assert.True(parser.TryParse(out var input));
        chat.HandleKey(input.Key);
        Assert.True(pending.Task.IsCompleted);
        Assert.Equal(expected, Assert.IsType<ApprovalOutcome>(await pending.Task));
        Assert.False(chat.ExitRequested);
    }

    [Fact]
    public async Task ActivePrompt_PreemptsGlobalShortcutsPasteAndMouse_WithoutSendingChat()
    {
        using var chat = await CreateChat();
        var pane = chat.InputPane;
        pane.Input = "draft";
        pane.Cursor = 2;
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.ShowQuestionPrompt(new([new("q", "Question", Options: [new("A")])]), pending);
        chat.HandleKey(new('\0', ConsoleKey.X, false, false, true));
        chat.HandleKey(new('\0', ConsoleKey.Q, false, false, false));
        chat.HandlePaste("custom answer");
        var layout = LayoutEngine.Calculate(80, 24);
        chat.Draw(new CellGrid(80, 24), layout);
        chat.HandleMouseClick(70, 22, layout);
        Assert.False(pending.Task.IsCompleted);
        Assert.Equal("", pane.Input);
        Assert.False(chat.ExitRequested);
        chat.HandleKey(Key(ConsoleKey.Enter));
        var answer = Assert.IsType<AskUserQuestionAnswer>(await pending.Task);
        Assert.Equal("custom answer", Assert.Single(answer.Answers).Custom);
        Assert.Equal("draft", pane.Input);
        Assert.Equal(2, pane.Cursor);
    }

    [Fact]
    public async Task CompletedRequest_ClosesPromptAndRestoresDraft_BeforeLateInput()
    {
        using var chat = await CreateChat();
        var pane = chat.InputPane;
        pane.Input = "draft";
        pane.Cursor = 1;
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.ShowQuestionPrompt(new([new("q", "Question", Options: [new("A")])]), pending);
        pending.SetResult(AskUserQuestionAnswer.Dismissed);
        chat.DrainUi();
        Assert.False(pane.HasChoicePrompt);
        Assert.Equal("draft", pane.Input);
        Assert.Equal(1, pane.Cursor);
        Assert.True(Assert.IsType<AskUserQuestionAnswer>(await pending.Task).IsDismissed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_AbortsTheActualQuestionAndCannotReopenThePrompt(bool cancelBeforeUiDrain)
    {
        using var chat = await CreateChat();
        using var cancellation = new CancellationTokenSource();
        var pane = chat.InputPane;
        pane.Input = "saved 中文 draft";
        pane.Cursor = 3;
        var service = chat.Ctx.Get<UserQuestionService>(UserQuestionService.ServiceName)!;
        var pending = service.Ask(new([new("q", "Choose", Options: [new("A")])]), cancellation.Token);
        try
        {
            if (!cancelBeforeUiDrain)
            {
                chat.DrainUi();
                Assert.True(pane.HasChoicePrompt);
            }
            cancellation.Cancel();
            var error = await Assert.ThrowsAsync<UserQuestionException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(UserQuestionException.AskAborted, error.Code);
            chat.DrainUi();
            Assert.False(pane.HasChoicePrompt);
            Assert.Null(pane.PendingQuestions);
            Assert.Equal("saved 中文 draft", pane.Input);
            Assert.Equal(3, pane.Cursor);
            Assert.False(chat.ExitRequested);
        }
        finally
        {
            chat.DrainUi();
            if (pane.HasChoicePrompt)
                chat.HandleKey(Key(ConsoleKey.Escape));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_ClosesTheActualApprovalAndRestoresDraft(bool cancelBeforeUiDrain)
    {
        using var chat = await CreateChat();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pane = chat.InputPane;
        pane.Input = "saved approval draft";
        pane.Cursor = 4;
        pane.Agent.Session.Append(new TurnStartPayload(1));
        var service = ApprovalService.Register(chat.Ctx);
        var waiting = service.Request(new(pane.Agent, "bash", ToolCallId.Create("cancel-approval")), cancellation.Token);
        if (!cancelBeforeUiDrain)
        {
            chat.DrainUi();
            Assert.True(pane.HasChoicePrompt);
        }
        cancellation.Cancel();
        Assert.Equal(ApprovalOutcome.Cancelled, await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        chat.DrainUi();
        Assert.False(pane.HasChoicePrompt);
        Assert.Null(pane.PendingApproval);
        Assert.Equal("saved approval draft", pane.Input);
        Assert.Equal(4, pane.Cursor);
        Assert.False(chat.ExitRequested);
        Assert.False(ApprovalService.HasGrant(pane.Agent.Session, "bash"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualToolAndQuestionService_ConsumeTuiAnswersOrWholeGroupDismissal(bool dismiss)
    {
        using var chat = await CreateChat();
        using var tool = AskUserTool.Register(chat.Ctx);
        chat.InputPane.Input = "saved 中文";
        chat.InputPane.Cursor = 2;
        var tools = chat.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var execution = tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("choice-tool"),
            Name = "ask_user_question",
            Arguments = JsonDocument.Parse("""
                {"questions":[
                  {"id":"single","question":"Single","options":[{"label":"Disabled","disabled":true},{"label":"Chosen","value":"chosen"}]},
                  {"id":"multi","question":"Multi","multi_select":true,"options":[{"label":"A","value":"a","default":true},{"label":"B","value":"b"}]},
                  {"id":"custom","question":"Custom"}]}
                """).RootElement,
            Agent = chat.InputPane.Agent,
            Signal = TestContext.Current.CancellationToken,
        });
        chat.DrainUi();
        Assert.True(chat.InputPane.HasChoicePrompt);
        chat.HandleKey(Key(ConsoleKey.Enter));
        if (dismiss)
            chat.HandleKey(Key(ConsoleKey.Escape));
        else
        {
            chat.HandleKey(Key(ConsoleKey.D2, '2'));
            chat.HandleKey(Key(ConsoleKey.Enter));
            chat.HandlePaste("自定义 answer");
            chat.HandleKey(Key(ConsoleKey.Enter));
        }
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        using var output = JsonDocument.Parse(Assert.IsType<TextBlock>(result.Content[0]).Text);
        var body = output.RootElement;
        Assert.Equal(dismiss ? "dismissed" : "answered", body.GetProperty("status").GetString());
        var answers = body.GetProperty("answers");
        if (dismiss)
        {
            Assert.Equal(0, answers.GetArrayLength());
            Assert.Equal("user_cancelled", body.GetProperty("reason").GetString());
        }
        else
        {
            Assert.Equal("chosen", answers[0].GetProperty("selected")[0].GetString());
            Assert.Equal(["a", "b"], answers[1].GetProperty("selected").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.Equal("自定义 answer", answers[2].GetProperty("custom").GetString());
        }
        Assert.Equal("saved 中文", chat.InputPane.Input);
        Assert.Equal(2, chat.InputPane.Cursor);
    }

    [Fact]
    public async Task PointerHover_WithoutPromptOrWithoutFocusChange_DoesNotRequestRedraw()
    {
        using var chat = await CreateChat();
        var before = chat.RenderVersion;
        chat.HandleMouseMove(3, 5);
        Assert.Equal(before, chat.RenderVersion);
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.InputPane.ShowQuestionPrompt(new([new("q", "Pick", Options: [new("A"), new("B")])]), pending);
        chat.Draw(new CellGrid(80, 24), LayoutEngine.Calculate(80, 24));
        before = chat.RenderVersion;
        chat.HandleMouseMove(1, 2);
        Assert.Equal(before, chat.RenderVersion);
        chat.HandleMouseMove(1, 3);
        Assert.True(chat.RenderVersion > before);
        before = chat.RenderVersion;
        chat.HandleMouseMove(2, 3);
        Assert.Equal(before, chat.RenderVersion);
        chat.HandleKey(Key(ConsoleKey.Escape));
    }

    private async Task<ChatWindow> CreateChat()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        UserQuestionService.Register(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        CommandsService.Register(ctx);
        var handle = await agents.Create(new CreateAgentOptions(SessionId.Create($"choice-{Guid.NewGuid():N}"), null, new AgentOptions("deepseek-official", "deepseek-v4-flash")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        return new ChatWindow(ctx, agent, HarnessHome.Resolve(_home), null);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char character = '\0') => new(character, key, false, false, false);

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }
}
