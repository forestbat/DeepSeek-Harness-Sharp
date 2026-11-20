using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tui;
using Dsh.Tui.Services;

namespace Dsh.Tests;

/**
 * 提问/审批面板的按键路由: 面板出现期间方向键/空格/数字/回车/Esc 必须先给面板,
 * 不能被时间线/结果浮层或窗格聚焦吃掉; 面板关闭后这些键回到原有语义。
 */
[Collection("TuiTheme")]
public sealed class ChoicePromptKeyRoutingTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.Combine(TempTree.RepositoryRoot, "tests/.scratch/test-homes"),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task QuestionPanel_ArrowSpaceAndDigit_SelectAndSubmitMultiSelectAnswer()
    {
        using var chat = await CreateChat();
        var pending = NewAnswer();
        chat.InputPane.ShowQuestionPrompt(MultiQuestion(), pending);
        chat.HandleKey(Key(ConsoleKey.DownArrow));
        chat.HandleKey(Key(ConsoleKey.Spacebar, ' '));
        chat.HandleKey(Key(ConsoleKey.D3, '3'));
        chat.HandleKey(Key(ConsoleKey.Enter));
        var answer = await Answer(pending);
        Assert.Equal(["b", "c"], Assert.Single(answer.Answers).Selected);
        Assert.False(chat.InputPane.HasChoicePrompt);
    }

    [Fact]
    public async Task QuestionPanel_Digit_SelectsTheNthOptionOfASingleChoiceQuestion()
    {
        using var chat = await CreateChat();
        var pending = NewAnswer();
        chat.InputPane.ShowQuestionPrompt(
            new([new("q", "Pick", Options: [new("A", Value: "a"), new("B", Value: "b"), new("C", Value: "c")])]),
            pending);
        chat.HandleKey(Key(ConsoleKey.D2, '2'));
        chat.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal(["b"], Assert.Single((await Answer(pending)).Answers).Selected);
    }

    [Fact]
    public async Task QuestionPanel_EnterWithoutSelection_AdoptsTheHighlightedOption()
    {
        using var chat = await CreateChat();
        var pending = NewAnswer();
        // 单选: 回车即采纳高亮项。
        chat.InputPane.ShowQuestionPrompt(
            new([new("q", "Pick", Options: [new("A", Value: "a"), new("B", Value: "b")])]),
            pending);
        chat.HandleKey(Key(ConsoleKey.UpArrow));
        chat.HandleKey(Key(ConsoleKey.DownArrow));
        chat.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal(["b"], Assert.Single((await Answer(pending)).Answers).Selected);
    }

    /** 多选没有勾选就回车不能交卷: 否则用户会以为选上了, 实际提交的是空答案。 */
    [Fact]
    public async Task QuestionPanel_MultiSelectEnterWithoutSelection_DoesNotSubmit()
    {
        using var chat = await CreateChat();
        var pending = NewAnswer();
        chat.InputPane.ShowQuestionPrompt(
            new([new("q", "Pick", Options: [new("A", Value: "a"), new("B", Value: "b")], MultiSelect: true)]),
            pending);
        chat.HandleKey(Key(ConsoleKey.DownArrow));
        chat.HandleKey(Key(ConsoleKey.Enter));
        Assert.False(pending.Task.IsCompleted);
        Assert.True(chat.InputPane.HasChoicePrompt);
    }

    [Fact]
    public async Task QuestionPanel_SpaceAsPlainCharacter_TogglesTheFocusedOption()
    {
        using var chat = await CreateChat();
        var pending = NewAnswer();
        chat.InputPane.ShowQuestionPrompt(MultiQuestion(), pending);
        // GPU 窗口的文本事件没有键名, 空格只以字符形式到达。
        chat.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.NoName, false, false, false));
        chat.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal(["a"], Assert.Single((await Answer(pending)).Answers).Selected);
    }

    [Fact]
    public async Task QuestionPanel_TabFocusesCustomFieldAndEnterSubmitsTheTypedAnswer()
    {
        using var chat = await CreateChat();
        var pending = NewAnswer();
        chat.InputPane.ShowQuestionPrompt(MultiQuestion(), pending);
        chat.HandleKey(Key(ConsoleKey.Tab));
        chat.HandleKey(new ConsoleKeyInfo('x', ConsoleKey.NoName, false, false, false));
        chat.HandleKey(Key(ConsoleKey.Enter));
        var item = Assert.Single((await Answer(pending)).Answers);
        Assert.Equal("x", item.Custom);
        Assert.Empty(item.Selected);
    }

    [Fact]
    public async Task QuestionPanel_KeepsArrowsWhileTheResultOverlayIsOpen()
    {
        using var chat = await CreateChat();
        chat.InputPane.ShowResult("/session 结果", "第一行\n第二行", isError: false);
        var pending = NewAnswer();
        chat.InputPane.ShowQuestionPrompt(MultiQuestion(), pending);
        chat.HandleKey(Key(ConsoleKey.DownArrow));
        chat.HandleKey(Key(ConsoleKey.Spacebar, ' '));
        chat.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal(["b"], Assert.Single((await Answer(pending)).Answers).Selected);
    }

    [Fact]
    public async Task QuestionPanel_KeepsArrowsWhileTheTimelineOverlayIsOpen()
    {
        using var chat = await CreateChat();
        var executed = "";
        chat.InputPane.OpenTimeline(
            [new CommandCandidate("#1", "first"), new CommandCandidate("#2", "second")],
            (action, value) => executed = $"{action} {value}");
        var pending = NewAnswer();
        chat.InputPane.ShowQuestionPrompt(MultiQuestion(), pending);
        chat.HandleKey(Key(ConsoleKey.DownArrow));
        chat.HandleKey(Key(ConsoleKey.Spacebar, ' '));
        chat.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal(["b"], Assert.Single((await Answer(pending)).Answers).Selected);
        Assert.Equal("", executed);
    }

    [Fact]
    public async Task ClosedPanel_ReturnsTheKeysToTheOverlayBelow()
    {
        using var chat = await CreateChat();
        var executed = "";
        chat.InputPane.OpenTimeline(
            [new CommandCandidate("#1", "first"), new CommandCandidate("#2", "second")],
            (action, value) => executed = $"{action} {value}");
        var pending = NewAnswer();
        chat.InputPane.ShowQuestionPrompt(MultiQuestion(), pending);
        chat.HandleKey(Key(ConsoleKey.Escape));
        Assert.True(Assert.IsType<AskUserQuestionAnswer>(await pending.Task).IsDismissed);
        chat.HandleKey(Key(ConsoleKey.DownArrow));
        chat.HandleKey(Key(ConsoleKey.Enter));
        chat.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal("jump #2", executed);
    }

    [Fact]
    public async Task ApprovalPanel_ArrowsAndEnter_StillHonorTheDecisionShortcuts()
    {
        using var chat = await CreateChat();
        var pending = NewAnswer();
        chat.InputPane.ShowApprovalPrompt(
            new(chat.InputPane.Agent, "bash", ToolCallId.Create("routing-approval"), "Run a command"),
            pending);
        chat.HandleKey(Key(ConsoleKey.DownArrow));
        chat.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal(ApprovalOutcome.AllowedForSession, Assert.IsType<ApprovalOutcome>(await pending.Task));

        var shortcut = NewAnswer();
        chat.InputPane.ShowApprovalPrompt(
            new(chat.InputPane.Agent, "bash", ToolCallId.Create("routing-shortcut"), "Run a command"),
            shortcut);
        chat.HandleKey(Key(ConsoleKey.N, 'n'));
        Assert.Equal(ApprovalOutcome.Rejected, Assert.IsType<ApprovalOutcome>(await shortcut.Task));
    }

    private static AskUserQuestionRequest MultiQuestion()
        => new([new("q", "Pick",
            Options: [new("A", Value: "a"), new("B", Value: "b"), new("C", Value: "c")],
            MultiSelect: true)]);

    private static TaskCompletionSource<object?> NewAnswer()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<AskUserQuestionAnswer> Answer(TaskCompletionSource<object?> pending)
        => Assert.IsType<AskUserQuestionAnswer>(
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

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
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"routing-{Guid.NewGuid():N}"), null, new AgentOptions("deepseek-official", "deepseek-v4-flash")));
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
