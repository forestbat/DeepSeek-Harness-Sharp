using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

/** GUI 里可独立验证的组件: 审批提示、消息折叠、token 统计、统一决策窗口。 */
public sealed class GuiComponentTests
{
    [Fact]
    public void ApprovalHints_PickPrimaryArgument_And_FlagDestructiveCommands()
    {
        var arguments = """{"command":"rm -rf ./build","description":"清理"}""";

        Assert.Equal("rm -rf ./build", ApprovalHints.PrimaryArgument("bash", arguments));
        Assert.StartsWith("⚠", ApprovalHints.Impact("bash", arguments), StringComparison.Ordinal);
        Assert.Equal("", ApprovalHints.Impact("read", """{"path":"/tmp/a"}"""));
    }

    [Fact]
    public void ApprovalHints_FallBackToRawJson_WhenNothingRecognized()
    {
        Assert.Equal("""{"x":1}""", ApprovalHints.PrimaryArgument("custom", """{"x":1}"""));
        Assert.Equal("plain text", ApprovalHints.PrimaryArgument("custom", "plain text"));
        Assert.Equal("", ApprovalHints.PrimaryArgument("custom", null));
    }

    [Fact]
    public void MessageViewModel_FoldsReasoningAndTools_ButNeverAssistantBody()
    {
        var reasoning = new MessageViewModel("思考", new string('x', MessageViewModel.PreviewChars + 1), MessageKind.Reasoning, false);
        var assistant = new MessageViewModel("助手", "正文", MessageKind.Assistant, false);

        Assert.True(reasoning.IsFoldable);
        Assert.False(reasoning.IsExpanded);
        Assert.True(reasoning.ShowPreview);
        Assert.False(reasoning.ShowBody);
        reasoning.ToggleFoldCommand.Execute(null);
        Assert.True(reasoning.ShowBody);

        var shortReasoning = new MessageViewModel("思考", "第一行\n第二行", MessageKind.Reasoning, false);
        Assert.True(shortReasoning.IsFoldable);
        Assert.True(shortReasoning.IsExpanded);

        var streaming = new MessageViewModel("思考", "", MessageKind.Reasoning, true);
        Assert.False(streaming.IsExpanded);

        Assert.False(assistant.IsFoldable);
        Assert.True(assistant.ShowBody);
        Assert.True(assistant.ShowMarkdown);
        Assert.True(assistant.ShowActions);
    }

    [Fact]
    public void MessageViewModel_SystemMessages_FoldOnlyBeyondPreviewChars()
    {
        var shortMessage = new MessageViewModel("系统", "导出完成", MessageKind.System, false);

        Assert.True(shortMessage.IsFoldable);
        Assert.True(shortMessage.IsExpanded);
        Assert.False(shortMessage.ShowPreview);

        var longMessage = new MessageViewModel("系统", new string('x', MessageViewModel.PreviewChars + 1), MessageKind.System, false);

        Assert.True(longMessage.IsFoldable);
        Assert.False(longMessage.IsExpanded);
        Assert.True(longMessage.ShowPreview);
        Assert.False(longMessage.ShowBody);
        longMessage.ToggleFoldCommand.Execute(null);
        Assert.True(longMessage.ShowBody);
    }

    [Fact]
    public void MessageViewModel_PreviewIsSingleLine_AndCapped()
    {
        var message = new MessageViewModel("助手", $"第一行\n{new string('x', 300)}", MessageKind.Result, false);

        Assert.DoesNotContain('\n', message.Preview);
        Assert.EndsWith("…", message.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageViewModel_ContextInjection_KeepsLabelAndDetailSeparate()
    {
        var message = new MessageViewModel("", "上下文注入 · @pkg", MessageKind.Context, false) { Detail = "注入正文" };

        Assert.Equal("上下文注入 · @pkg", message.Text);
        Assert.True(message.HasDetail);
        Assert.True(message.ShowDetail);
        message.ToggleFoldCommand.Execute(null);
        Assert.False(message.ShowDetail);
        Assert.True(message.ShowPreview);
        Assert.False(message.HasRole);
    }

    [Fact]
    public void TokenStats_TrackUsageLatencyAndCache()
    {
        var stats = new TokenStatsViewModel();
        stats.ObserveTurn(2, 3);
        Assert.Equal("轮 2 · 步 3", stats.Rounds);

        stats.RequestStarted();
        stats.FirstTokenArrived();
        Assert.NotEqual(TokenStatsViewModel.Empty, stats.FirstToken);

        stats.ObserveUsage(new TokenUsage(1000, 200, CacheReadTokens: 500));
        Assert.Equal("in 1.0k / out 200", stats.Totals);
        Assert.Equal("缓存命中 50%", stats.Cache);
        // 重放历史会话时事件瞬间连发, 这时算出来的 tok/s 没有意义, 应当留空。
        Assert.Equal(TokenStatsViewModel.Empty, stats.TokensPerSecond);

        Thread.Sleep(150);
        stats.ObserveUsage(new TokenUsage(1000, 200, CacheReadTokens: 500));
        Assert.NotEqual(TokenStatsViewModel.Empty, stats.TokensPerSecond);

        stats.RequestFinished();
        Assert.NotEqual(TokenStatsViewModel.Empty, stats.Latency);
    }

    [Fact]
    public void TokenStats_IgnoreEmptyUsage()
    {
        var stats = new TokenStatsViewModel();

        stats.ObserveUsage(new TokenUsage(0, 0));

        Assert.Equal(TokenStatsViewModel.Empty, stats.Totals);
    }

    [Fact]
    public async Task DecisionViewModel_ApprovalOutcomes_FollowTheClickedAction()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var request = new ApprovalRequest(
            environment.Agent,
            "bash",
            ToolCallId.Create("call-1"),
            "需要执行命令",
            """{"command":"git push --force"}""");
        var decision = DecisionViewModel.ForApproval(request);

        Assert.True(decision.IsApproval);
        Assert.Equal("需要审批", decision.Heading);
        Assert.Equal("bash", decision.ToolName);
        Assert.Equal("git push --force", decision.Command);
        Assert.True(decision.HasImpact);

        decision.AllowAlwaysCommand.Execute(null);
        Assert.Equal(ApprovalOutcome.AllowedForSession, decision.Result);

        var rejected = DecisionViewModel.ForApproval(request);
        rejected.RejectCommand.Execute(null);
        Assert.Equal(ApprovalOutcome.Rejected, rejected.Result);

        var cancelled = DecisionViewModel.ForApproval(request);
        cancelled.CancelCommand.Execute(null);
        Assert.Equal(ApprovalOutcome.Cancelled, cancelled.Result);
    }

    [Fact]
    public async Task DecisionViewModel_SingleSelectReplaces_AndSubmitNeedsAnswer()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var question = new AskUserQuestionItem(
            "q1",
            "选择策略",
            Options: [new AskUserQuestionOption("A"), new AskUserQuestionOption("B")]);
        var decision = DecisionViewModel.ForQuestion(new AskUserQuestionRequest([question], environment.Agent));
        var single = Assert.Single(decision.Questions);
        Assert.False(decision.CanSubmit);

        single.SelectCommand.Execute(single.Options[0]);
        Assert.True(single.Options[0].IsSelected);
        Assert.True(decision.CanSubmit);

        single.SelectCommand.Execute(single.Options[1]);
        Assert.False(single.Options[0].IsSelected);
        Assert.True(single.Options[1].IsSelected);

        decision.SubmitCommand.Execute(null);
        var answer = Assert.IsType<AskUserQuestionAnswer>(decision.Result);
        Assert.Equal("B", Assert.Single(Assert.Single(answer.Answers).Selected));
    }

    [Fact]
    public async Task DecisionViewModel_MultiSelectKeepsBoth_AndCustomTextCountsAsAnswer()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var question = new AskUserQuestionItem(
            "q2",
            "选择多项",
            Options: [new AskUserQuestionOption("A"), new AskUserQuestionOption("B")],
            MultiSelect: true);
        var decision = DecisionViewModel.ForQuestion(new AskUserQuestionRequest([question], environment.Agent));
        var single = Assert.Single(decision.Questions);

        single.SelectCommand.Execute(single.Options[0]);
        single.SelectCommand.Execute(single.Options[1]);

        var answer = decision.BuildAnswer()!;
        Assert.Equal(2, Assert.Single(answer.Answers).Selected.Count);

        var free = new AskUserQuestionItem("q3", "怎么做");
        var freeDecision = DecisionViewModel.ForQuestion(new AskUserQuestionRequest([free], environment.Agent));
        Assert.False(freeDecision.CanSubmit);
        freeDecision.Questions[0].CustomText = "按我的方案来";
        Assert.True(freeDecision.CanSubmit);
        Assert.Equal("按我的方案来", freeDecision.BuildAnswer()!.Answers[0].Custom);
    }
}
