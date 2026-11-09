using Dsh.Gui.ViewModels;

namespace Dsh.Tests;

/**
 * 折叠相关的行为契约(用户视角, 真实会话):
 *  1) 折叠按钮在项内的位置不随展开/折叠变化 —— 否则用户点了按钮, 按钮自己挪走, 第二次点击就落到正文上;
 *  2) 贴底(跟随中)时点折叠, 被点那条停在原地, 并且脱离跟随、"回到最新"按钮出现;
 *  3) 点"回到最新"回到末尾并恢复跟随。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class FoldAnchorContractTests
{
    private const double Tolerance = 1;

    /** 折叠按钮相对所属消息项顶端的偏移, 展开前后必须一致(五种可折叠消息共用同一模板)。 */
    [Theory]
    [InlineData(MessageKind.Tool)]
    [InlineData(MessageKind.Result)]
    [InlineData(MessageKind.Reasoning)]
    public Task ButtonKeepsItsPlaceInsideTheItem(MessageKind kind) => HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await RealChatScene.CreateAsync();
        var message = new MessageViewModel(
            kind == MessageKind.Tool ? "工具" : kind == MessageKind.Result ? "结果" : "思考",
            kind == MessageKind.Tool ? "read" : LongText(),
            kind,
            false);
        scene.ViewModel.Messages.Add(message);
        scene.ViewModel.Messages.Add(new MessageViewModel("助手", "尾行", MessageKind.Assistant, false));
        scene.Render();
        scene.Settle();

        var target = scene.EnsureFoldButtonVisible(message);
        Assert.True(target is not null, "找不到折叠按钮");
        var item = scene.Container(message)!;
        var offsetBefore = scene.TopOf(target!.Value.Button) - scene.TopOf(item);
        var expandedBefore = message.IsExpanded;

        scene.Tap(target.Value.Button);
        scene.Pump(6);

        Assert.NotEqual(expandedBefore, message.IsExpanded);
        var after = scene.EnsureFoldButtonVisible(message);
        Assert.True(after is not null, "展开后找不到折叠按钮");
        var offsetAfter = scene.TopOf(after!.Value.Button) - scene.TopOf(scene.Container(message)!);
        Assert.True(Math.Abs(offsetAfter - offsetBefore) <= Tolerance,
            $"{kind}: 按钮在项内移动了 {offsetBefore:0} → {offsetAfter:0}");
    });

    /** 贴底时点折叠: 被点那条不动, 且明确脱离跟随并给出回到最新的入口。 */
    [Fact]
    public Task TogglingWhileStuckToTheBottom_KeepsTheMessageAndLeavesFollowMode() => HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await RealChatScene.CreateAsync(width: 1440, height: 1000);
        // 真实会话常以工具结果收尾; 补一条可折叠的尾行, 让"贴底 + 可折叠"的组合成立。
        scene.ViewModel.Messages.Add(new MessageViewModel("工具", "read", MessageKind.Tool, false)
        {
            ToolName = "read",
            ArgumentText = "{\"path\":\"ChatView.axaml.cs\"}",
        });
        scene.ViewModel.Messages.Add(new MessageViewModel("助手", "尾行", MessageKind.Assistant, false));
        scene.Render();
        scene.Settle();
        scene.ScrollToFraction(1);
        scene.Settle();
        Assert.True(scene.IsSticky, "precondition: 视口必须处于跟随(贴底)状态");

        var candidates = scene.VisibleFoldButtons();
        Assert.True(candidates.Count > 0, "贴底时视口里没有可折叠消息");
        var pick = candidates[^1];
        var item = scene.Container(pick.Message)!;
        var topBefore = scene.TopOf(item);

        scene.Tap(pick.Button);
        var worst = 0d;
        for (var frame = 0; frame < 10; frame++)
        {
            scene.Pump(1);
            if (scene.Container(pick.Message) is not { } container)
                break;
            worst = Math.Max(worst, Math.Abs(scene.TopOf(container) - topBefore));
        }

        Assert.True(worst <= Tolerance, $"被点那条被顶走 {worst:0}px");
        Assert.False(scene.IsSticky, "点折叠后应脱离跟随, 否则下一次内容增长会把它拽走");
        Assert.True(scene.JumpToLatestVisible, "脱离跟随后必须给出“回到最新”入口");
    }, TimeSpan.FromMinutes(5));

    /** "回到最新"把视口送回末尾并恢复跟随。 */
    [Fact]
    public Task JumpToLatest_ReturnsToTheEndAndResumesFollowing() => HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await RealChatScene.CreateAsync();
        scene.ScrollToFraction(1);
        scene.Settle();
        // 跟随意图只由**用户输入**改判(程序写 offset 不算), 所以这里用真实滚轮离开底部。
        scene.Wheel(6);
        scene.Render();
        Assert.False(scene.IsSticky, "precondition: 滚轮离开底部后不再跟随");
        Assert.True(scene.JumpToLatestVisible, "离开底部后应出现“回到最新”");

        Assert.True(scene.ClickJumpToLatest(), "按钮不可点");
        scene.Pump(3);

        Assert.True(Math.Abs(scene.MaximumOffset - scene.Offset) <= Tolerance,
            $"没有回到末尾: offset {scene.Offset:0} / 最大 {scene.MaximumOffset:0}");
        Assert.True(scene.IsSticky, "回到末尾后应恢复跟随");
        Assert.False(scene.JumpToLatestVisible, "已在底部时不该再显示按钮");
    }, TimeSpan.FromMinutes(5));

    /**
     * 展开一条比视口还高的消息: 它会涨上千像素, 面板会把它自己甩出实体化窗口。
     * 它必须仍停在原来的屏幕位置(用户点开它就是为了看它, 不能点了反而看不见)。
     */
    [Fact]
    public Task ExpandingAMessageTallerThanTheViewport_KeepsItInPlace() => HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await RealChatScene.CreateAsync(width: 900, height: 520);
        scene.ScrollToFraction(1);
        scene.Settle();
        // 真实会话里最长的可折叠消息(实测 4163 字符的思维链展开约 1230px, 视口只有 282px)。
        var longText = new System.Text.StringBuilder();
        for (var line = 0; line < 80; line++)
            longText.AppendLine($"第 {line} 行: 这是一段很长的正文, 展开后整条消息比视口还高很多。");
        var message = new MessageViewModel("思考", longText.ToString(), MessageKind.Reasoning, false);
        scene.ViewModel.Messages.Add(message);
        scene.ViewModel.Messages.Add(new MessageViewModel("助手", "尾行", MessageKind.Assistant, false));
        scene.Render();
        scene.Settle();

        var target = scene.EnsureFoldButtonVisible(message);
        Assert.True(target is not null, "视口里找不到这条的折叠按钮");
        var topBefore = scene.TopOf(scene.Container(message)!);
        Assert.False(message.IsExpanded, "precondition: 起点应为折叠态");

        scene.Tap(target!.Value.Button);
        var worst = 0d;
        var missing = false;
        for (var frame = 0; frame < 12; frame++)
        {
            scene.Pump(1);
            if (scene.Container(message) is not { } container)
            {
                missing = true;
                continue;
            }
            worst = Math.Max(worst, Math.Abs(scene.TopOf(container) - topBefore));
        }

        Assert.True(message.IsExpanded);
        Assert.False(missing, "展开后这条被虚拟化甩出了视口, 用户点开却看不见内容");
        Assert.True(worst <= Tolerance, $"展开后位置漂了 {topBefore:0} → {topBefore + worst:0}");
        var height = scene.Container(message)!.Bounds.Height;
        Assert.True(height > scene.Viewport, $"precondition: 这条展开后应高于视口(实际 {height:0} / {scene.Viewport:0})");
    }, TimeSpan.FromMinutes(5));

    private static string LongText()
    {
        var builder = new System.Text.StringBuilder();
        for (var line = 0; line < 12; line++)
            builder.AppendLine($"第 {line} 行: 这是一段足够长、折叠后必须点开才能读完的正文。");
        return builder.ToString();
    }
}
