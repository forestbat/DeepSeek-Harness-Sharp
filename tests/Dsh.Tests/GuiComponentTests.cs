using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.ViewModels;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

/** GUI 里可独立验证的组件: 审批提示、消息折叠、token 统计、统一决策窗口。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class GuiComponentTests
{
    [Fact]
    public void NetworkProbe_HasNoPlaceholderBeforeTheFirstRun()
    {
        var probe = new NetworkProbeViewModel(ProxyProbeLayer.Ssh);

        Assert.False(probe.HasResult);
        Assert.Equal("", probe.OutcomeText);
    }

    /** Just above TokenStatsViewModel's 0.1 s floor for a measurable request. */
    private static readonly TimeSpan MeasurableRequest = TimeSpan.FromMilliseconds(150);

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
        var clock = new ManualClock();
        var stats = new TokenStatsViewModel(clock);
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

        clock.Advance(MeasurableRequest);
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

    /** 上下文占用取最近一次请求的提示词, 与累计 in/out 是两条独立的口径。 */
    [Fact]
    public void TokenStats_ShowContextOccupancySeparatelyFromTotals()
    {
        var stats = new TokenStatsViewModel();
        Assert.Equal(TokenStatsViewModel.Empty, stats.ContextText);

        stats.ObserveRequestContext(128_000);
        stats.ObserveUsage(new TokenUsage(10_000, 200));
        stats.ObserveUsage(new TokenUsage(20_000, 300));

        Assert.Equal("上下文 20.0k/128.0k (16%)", stats.ContextText);
        // 累计口径不受影响: 两次请求的输入相加。
        Assert.Equal("in 30.0k / out 500", stats.Totals);
    }

    [Fact]
    public void TokenStats_ContextOmitsPercentage_WhenWindowUnknown()
    {
        var stats = new TokenStatsViewModel();

        stats.ObserveRequestContext(null);
        stats.ObserveUsage(new TokenUsage(7_200, 10));

        Assert.Equal("上下文 7.2k", stats.ContextText);
    }

    [Fact]
    public void TokenStats_Reset_ClearsContextOccupancy()
    {
        var stats = new TokenStatsViewModel();
        stats.ObserveRequestContext(64_000);
        stats.ObserveUsage(new TokenUsage(1_000, 10));

        stats.Reset();

        Assert.Equal(TokenStatsViewModel.Empty, stats.ContextText);
    }

    [Fact]
    public async Task DecisionViewModel_ApprovalOutcomes_FollowTheClickedAction()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
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
        await using var environment = await GuiTestEnvironment.CreateAsync();
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
        await using var environment = await GuiTestEnvironment.CreateAsync();
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

        single.CustomText = "补充说明";
        var combined = decision.BuildAnswer()!.Answers[0];
        Assert.Equal(2, combined.Selected.Count);
        Assert.Equal("补充说明", combined.Custom);
    }

    [Fact]
    public void DecisionQuestions_UseSharedDefaultsDisabledValuesAndCustomPolicy()
    {
        var decision = DecisionViewModel.ForQuestion(new AskUserQuestionRequest(
        [
            new("single", "Choose", Options:
            [
                new("Unavailable", Value: "disabled", Disabled: true, Default: true),
                new("First", Value: "first", Default: true),
                new("Second", Value: "second", Default: true),
            ], AllowCustom: false),
            new("multi", "Choose several", Options:
            [
                new("Tests", Value: "tests", Default: true),
                new("Docs", Value: "docs", Default: true),
                new("Unavailable", Value: "no", Disabled: true),
            ], MultiSelect: true),
        ]), "request-choices");
        var single = decision.Questions[0];
        var multi = decision.Questions[1];
        Assert.Equal("request-choices", single.State.Prompt.RequestId);
        Assert.False(single.Options[0].IsEnabled);
        Assert.False(single.Options[0].IsSelected);
        Assert.True(single.Options[1].IsSelected);
        Assert.False(single.Options[2].IsSelected);
        single.SelectCommand.Execute(single.Options[0]);
        single.CustomText = "Not permitted";
        Assert.Equal("", single.CustomText);
        Assert.False(single.AllowCustom);
        Assert.Equal(["first"], single.BuildAnswer().Selected);
        Assert.True(decision.CanSubmit);

        single.SelectCommand.Execute(single.Options[2]);
        multi.SelectCommand.Execute(multi.Options[2]);
        multi.CustomText = "  Additional work  ";
        decision.SubmitCommand.Execute(null);
        var answer = Assert.IsType<AskUserQuestionAnswer>(decision.Result);
        Assert.Equal("answered", answer.Status);
        Assert.Equal(["second"], answer.Answers[0].Selected);
        Assert.Null(answer.Answers[0].Custom);
        Assert.Equal(["tests", "docs"], answer.Answers[1].Selected);
        Assert.Equal("Additional work", answer.Answers[1].Custom);
    }

    [Fact]
    public void QuestionCancellation_DiscardsPartialAnswers_AndLateSubmitCannotReplaceDismissal()
    {
        var decision = DecisionViewModel.ForQuestion(new AskUserQuestionRequest(
        [
            new("first", "Answered first", Options: [new("Yes", Value: "yes")]),
            new("second", "Still unanswered"),
        ]), "dismiss-request");
        var closed = 0;
        decision.Closed += _ => closed++;
        decision.Questions[0].SelectCommand.Execute(decision.Questions[0].Options[0]);
        decision.CancelCommand.Execute(null);
        decision.Questions[1].CustomText = "Late answer";
        decision.SubmitCommand.Execute(null);
        decision.Resolve(new AskUserQuestionAnswer([new("second", ["late"])]));

        var result = Assert.IsType<AskUserQuestionAnswer>(decision.Result);
        Assert.Equal("dismissed", result.Status);
        Assert.Equal("user_cancelled", result.Reason);
        Assert.Empty(result.Answers);
        Assert.True(decision.IsClosed);
        Assert.False(decision.CanSubmit);
        Assert.False(decision.SubmitCommand.CanExecute(null));
        Assert.Equal(1, closed);
    }

    [Fact]
    public void AuthoritativeInvalidation_ClosesOnce_AndDisablesLateApproval()
    {
        var decision = DecisionViewModel.ForApproval("bash", """{"command":"echo approval"}""", "Review", "approval-request");
        Assert.Equal("approval-request", decision.ApprovalState!.Prompt.RequestId);
        var closed = 0;
        decision.Closed += _ => closed++;
        decision.Invalidate();
        decision.AllowAlwaysCommand.Execute(null);
        decision.AllowOnceCommand.Execute(null);
        decision.Resolve(ApprovalOutcome.AllowedOnce);
        Assert.Equal(ApprovalOutcome.Cancelled, decision.Result);
        Assert.Equal(["cancel"], decision.ApprovalState.SelectedValues());
        Assert.True(decision.IsSuperseded);
        Assert.False(decision.AllowAlwaysCommand.CanExecute(null));
        Assert.Equal(1, closed);
    }

    [Theory]
    [InlineData(false, false, true, ApprovalOutcome.Rejected)]
    [InlineData(true, false, true, ApprovalOutcome.AllowedOnce)]
    [InlineData(true, true, false, ApprovalOutcome.AllowedForSession)]
    public void UnavailableApprovalChoices_CannotCloseOrChangeTheRepresentableDecision(bool once, bool session, bool reject, ApprovalOutcome expected)
    {
        var decision = DecisionViewModel.ForApproval("edit_file", "{}", "Review", "limited", once, session, reject);
        var closed = 0;
        decision.Closed += _ => closed++;
        if (!once)
            decision.AllowOnceCommand.Execute(null);
        if (!session)
            decision.AllowAlwaysCommand.Execute(null);
        if (!reject)
            decision.RejectCommand.Execute(null);
        Assert.False(decision.IsClosed);
        Assert.Null(decision.Result);
        Assert.Equal(0, closed);

        var command = expected switch
        {
            ApprovalOutcome.AllowedOnce => decision.AllowOnceCommand,
            ApprovalOutcome.AllowedForSession => decision.AllowAlwaysCommand,
            ApprovalOutcome.Rejected => decision.RejectCommand,
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };
        command.Execute(null);
        decision.AllowAlwaysCommand.Execute(null);
        Assert.Equal(expected, decision.Result);
        Assert.Equal(1, closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingCancellationCapability_KeepsDecisionOpen_UntilAnswerOrHostResolution(bool approval)
    {
        var decision = approval
            ? DecisionViewModel.ForApproval("bash", null, "Review", "unsupported")
            : DecisionViewModel.ForQuestion(new AskUserQuestionRequest([new("q", "Choose")]), "unsupported");
        decision.SetCancellationSupported(false);
        var closed = 0;
        decision.Closed += _ => closed++;

        Assert.False(decision.CancelCommand.CanExecute(null));
        Assert.Contains("Unsupported", decision.CancellationSupportMessage, StringComparison.Ordinal);
        decision.CancelCommand.Execute(null);
        decision.Resolve(null);
        decision.Resolve(approval ? ApprovalOutcome.Cancelled : AskUserQuestionAnswer.Dismissed);
        Assert.False(decision.IsClosed);
        Assert.Null(decision.Result);
        Assert.Equal(0, closed);

        decision.Invalidate();
        Assert.True(decision.IsClosed);
        Assert.True(decision.IsSuperseded);
        Assert.Equal(1, closed);
    }

    /** 选择模型提供者: 前 7 项是协议族(只带 Type), 其后是按 id 排序、带 ProviderId 的目录 provider; 两类都带段前缀。 */
    [Fact]
    public void ProviderChoices_Put_Protocol_Families_First_And_Tag_Both_Kinds()
    {
        var snapshot = ProviderCatalog.Parse(ProviderCatalogTests.Fixture);

        var choices = ProviderChoiceViewModel.Build(snapshot);
        var protocols = choices.Take(ProviderTypes.All.Count).ToList();

        Assert.Equal(ProviderTypes.All, protocols.Select(choice => choice.Type));
        Assert.All(protocols, choice => Assert.Null(choice.ProviderId));
        Assert.All(protocols, choice => Assert.StartsWith(ProviderChoiceViewModel.ProtocolPrefix, choice.Display, StringComparison.Ordinal));

        var catalog = choices.Skip(ProviderTypes.All.Count).ToList();
        Assert.Equal(
            ["anthropic", "kimi-code-plan-global", "openai", "zhipuai-coding-plan"],
            catalog.Select(choice => choice.ProviderId));
        Assert.All(catalog, choice => Assert.NotNull(choice.Type));
        Assert.All(catalog, choice => Assert.StartsWith(ProviderChoiceViewModel.CatalogPrefix, choice.Display, StringComparison.Ordinal));
    }

    /** Test clock: time moves only when the test advances it. */
    private sealed class ManualClock : TimeProvider
    {
        private long _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _now;

        public void Advance(TimeSpan duration) => _now += duration.Ticks;
    }
}
