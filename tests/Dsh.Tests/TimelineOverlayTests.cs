using Dsh.Tui;

namespace Dsh.Tests;

/** /timestamp 时间线浮层: 两级选择、过滤、revert 确认、fork 免确认。 */
public sealed class TimelineOverlayTests
{
    private static IReadOnlyList<CommandCandidate> Candidates()
        =>
        [
            new("#3", "#3 10:00:00 第三条", "session message"),
            new("#2", "#2 09:59:00 第二条", "session message"),
            new("ide-group|1", "src/App.cs @ 09:58:00 (JetBrains) [git HEAD]", "history window; 1 match git HEAD"),
        ];

    [Fact]
    public void Open_StartsAtLatestEntry_AndEnterMovesToActionStage()
    {
        var overlay = new TimelineOverlay();
        overlay.Open(Candidates());
        Assert.True(overlay.IsActive);
        Assert.False(overlay.PickingAction);
        Assert.Equal("#3", overlay.Selected?.Value);

        Assert.Null(overlay.ConfirmSelection());
        Assert.True(overlay.PickingAction);
        Assert.Equal("jump", TimelineOverlay.Actions[overlay.ActionIndex]);
    }

    [Fact]
    public void JumpAndFork_ReturnImmediately_ButRevertAsksForConfirmation()
    {
        var overlay = new TimelineOverlay();
        overlay.Open(Candidates());
        overlay.ConfirmSelection();

        overlay.Move(2);
        Assert.Equal("fork", TimelineOverlay.Actions[overlay.ActionIndex]);
        Assert.Equal("fork", overlay.ConfirmSelection());

        overlay.Close();
        overlay.Open(Candidates());
        overlay.ConfirmSelection();
        overlay.Move(1);
        Assert.Null(overlay.ConfirmSelection());
        Assert.True(overlay.AwaitingConfirm);
        Assert.True(overlay.AnswerConfirm(true));
        Assert.False(overlay.AwaitingConfirm);
    }

    [Fact]
    public void EscapeWalksBackOneLevelAtATime()
    {
        var overlay = new TimelineOverlay();
        overlay.Open(Candidates());
        overlay.ConfirmSelection();

        overlay.Back();
        Assert.True(overlay.IsActive);
        Assert.False(overlay.PickingAction);

        overlay.Back();
        Assert.False(overlay.IsActive);
    }

    [Fact]
    public void Filter_NarrowsCandidates_AndBackspaceRestores()
    {
        var overlay = new TimelineOverlay();
        overlay.Open(Candidates());
        foreach (var character in "App.cs")
            overlay.AppendFilter(character);
        Assert.Single(overlay.Candidates);
        Assert.StartsWith("ide-group", overlay.Candidates[0].Value);

        overlay.BackspaceFilter();
        overlay.BackspaceFilter();
        overlay.BackspaceFilter();
        overlay.BackspaceFilter();
        overlay.BackspaceFilter();
        overlay.BackspaceFilter();
        Assert.Equal(3, overlay.Candidates.Count);
    }

    [Fact]
    public void IdeEntriesStayInTheList_SoTimelineSwitchStillWorks()
    {
        var overlay = new TimelineOverlay();
        overlay.Open(Candidates());
        Assert.Contains(overlay.Candidates, candidate => candidate.Value.StartsWith("ide-group", StringComparison.Ordinal));
    }
}
