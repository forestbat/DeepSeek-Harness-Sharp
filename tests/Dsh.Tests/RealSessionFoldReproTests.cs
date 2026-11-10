using System.Globalization;
using System.Text;
using Dsh.Gui.ViewModels;

namespace Dsh.Tests;

/**
 * 折叠/展开"多余跳转"的复现套件: 真实会话 + 生产链路 (Host → MainViewModel → ChatView + VirtualizingStackPanel)。
 *
 * 这一版**只取证不改判**: 每个用例把测量结果写进 artifacts/repro/real-fold/*.txt,
 * 断言只保证"场景本身可用"(真的读到会话、真的点到折叠按钮), 因此套件保持全绿, 不会挡住主流程。
 * 报告里的数字就是用户能看到的现象; 修复之后再把这些阈值升级成断言(见 plans/折叠底边跳转-复现报告.md)。
 *
 * 判据(全部是用户视角的屏幕量):
 *   展开/收起只改变被点那条自身的高度 —— 它上面的一切一像素都不该动, 它下面的一切整体位移它的高度变化量。
 *   扣掉这条预期之后仍然存在的位移, 就是"多余跳转"。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class RealSessionFoldReproTests
{
    /** 屏幕位置上"看得见"的容差(px): 1px 以内属于布局取整。 */
    private const double PositionTolerance = 2;

    /** 一次折叠/展开自身最多改变几百像素; 超出这个数的高度变化没有因果来源。 */
    private const double PlausibleFoldDelta = 400;

    /** 点击后观察几帧(每帧跑一次布局 + 派发队列)。 */
    private const int ObservedFrames = 10;

    private static readonly TimeSpan MatrixTimeout = TimeSpan.FromMinutes(20);

    private static readonly (string Label, double Width, double Height)[] WindowSizes =
    [
        ("1280x800", 1280, 800),
        ("900x520", 900, 520),
        ("1440x1000", 1440, 1000),
    ];

    /** 结果文件目录(项目内, 不写到项目外)。 */
    private static string OutputDirectory
    {
        get
        {
            var directory = Path.Combine(TempTree.RepositoryRoot, "artifacts", "repro", "real-fold");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private sealed record Sample(
        string Case,
        string Kind,
        string Target,
        int IndexFromEnd,
        bool ExpandedBefore,
        bool ExpandedAfter,
        double ClickedDrift,
        double HeightDelta,
        double MaxOtherResidual,
        double MaxTransientResidual,
        string WorstOther,
        double OffsetBefore,
        double OffsetAfter,
        double MaxOffsetSwing,
        double ExtentBefore,
        double ExtentAfter,
        double MaxExtentSwing)
    {
        /** 目标当时是否真的可点。点空了的样本不能算产品缺陷。 */
        public bool Clickable { get; init; } = true;

        /** 点击前视口还没自洽(要点第二、三次才点到)。 */
        public bool Unsettled { get; init; }

        /** 点击之后视口仍在动(位置/offset/extent 还在变): 用户看到的就是"一直抖"。 */
        public bool DriftsAfterSettle { get; init; }

        public string? HitReason { get; init; }

        public bool Sticky { get; init; }

        public bool Recycled { get; init; }

        public bool Flipped => ExpandedBefore != ExpandedAfter;

        public double ExtentResidual => Math.Abs(ExtentAfter - ExtentBefore) - Math.Abs(HeightDelta);

        public double PeakExtentResidual => MaxExtentSwing - Math.Abs(HeightDelta);

        public double OffsetResidual => Math.Abs(OffsetAfter - OffsetBefore) - Math.Abs(HeightDelta);

        public IEnumerable<string> Symptoms()
        {
            if (Unsettled)
                yield return "视口未自洽";
            if (Recycled)
                yield return "被点那条被回收";
            if (!Clickable)
            {
                yield return "点空了";
                yield break;
            }
            if (!Flipped)
                yield return "没翻转";
            if (Flipped && !Recycled && Math.Abs(ClickedDrift) > PositionTolerance)
                yield return "被点那条被顶走";
            if (Flipped && MaxOtherResidual > PositionTolerance)
                yield return "其他消息最终被挪";
            if (Flipped && MaxTransientResidual > PositionTolerance)
                yield return "其他消息瞬跳";
            if (Flipped && DriftsAfterSettle)
                yield return "点击后视口仍在动";
            if (ExtentResidual > PlausibleFoldDelta || PeakExtentResidual > PlausibleFoldDelta)
                yield return "总高度估算漂移";
            else if (OffsetResidual > PlausibleFoldDelta)
                yield return "滚动条抽动";
        }

        public bool Fails => Symptoms().Any();

        public string Symptom => string.Join("+", Symptoms().DefaultIfEmpty("ok"));

        public string Describe()
            => $"[{Symptom}] {Kind} {Case} target={Target} end#{IndexFromEnd} "
                + $"expanded {ExpandedBefore}→{ExpandedAfter} "
                + $"被点 {(Recycled ? "回收" : ClickedDrift.ToString("+0;-0;0", CultureInfo.InvariantCulture))} "
                + $"高变 {HeightDelta:+0;-0;0} "
                + $"其他位移 终{MaxOtherResidual:0}/峰{MaxTransientResidual:0} ({WorstOther}) "
                + $"offset {OffsetBefore:0}→{OffsetAfter:0} 摆{MaxOffsetSwing:0} 余{OffsetResidual:0} "
                + $"extent {ExtentBefore:0}→{ExtentAfter:0} 摆{MaxExtentSwing:0} 余{ExtentResidual:0}"
                + (Sticky ? " [贴底]" : "")
                + (HitReason is null ? "" : $" hit={HitReason}");
    }

    /** 底边附近逐档滚轮, 每次点视口里最下面那条可折叠消息(点开再收回)。 */
    [Fact]
    public Task TogglingTheLastMessageNearBottom() => Measure("toggling-last-near-bottom", async () =>
    {
        var cases = new List<Sample>();
        foreach (var (label, width, height) in WindowSizes)
        {
            await using var scene = await RealChatScene.CreateAsync(width: width, height: height);
            for (var wheel = 0; wheel <= 14; wheel++)
                cases.AddRange(TogglePair(scene, $"{label} wheel{wheel}", near: 2, Placement(scene, wheel)));
        }
        return cases;
    });

    /**
     * 贴底(跟随态)时点折叠: 用户正在看输出, 顺手点开上面一条。
     * 裁决是"保被点那条不动", 于是点完会脱离贴底并出现"回到最新"按钮。
     *
     * 这份会话的末条是助手消息(不可折叠), 贴底时视口底边没有折叠按钮。真实会话常常以工具结果收尾,
     * 所以这里在末尾补一条同形状的工具消息, 让"贴底 + 可折叠"这个组合真的成立。
     */
    [Fact]
    public Task TogglingWhileStuckToTheBottom() => Measure("toggling-while-stuck-to-bottom", async () =>
    {
        var cases = new List<Sample>();
        foreach (var (label, width, height) in WindowSizes)
        {
            await using var scene = await RealChatScene.CreateAsync(width: width, height: height);
            AppendToolTail(scene);
            foreach (var wheel in new[] { 0, 1, 2 })
            {
                var placement = Placement(scene, wheel, keepSticky: wheel == 0);
                if (!scene.IsSticky)
                    continue;
                cases.AddRange(TogglePair(scene, $"{label} 贴底 wheel{wheel}", near: 2, placement));
            }
        }
        return cases;
    });

    /** 在会话末尾补一条工具消息(与真实工具消息同一种) 与一条助手消息, 让末条可折叠。 */
    private static void AppendToolTail(RealChatScene scene)
    {
        scene.ViewModel.Messages.Add(new MessageViewModel("工具", "read", MessageKind.Tool, false)
        {
            ToolName = "read",
            ArgumentText = "{\"path\":\"ChatView.axaml.cs\"}",
        });
        scene.ViewModel.Messages.Add(new MessageViewModel("助手", "先看这一段。", MessageKind.Assistant, false));
        scene.Render();
        scene.Settle();
    }

    /** 视口顶端那条正要被点开时: 它一半在视口外, 补偿与估算都在这里最容易失手。 */
    [Fact]
    public Task TogglingTheMessageAtTheTopEdge() => Measure("toggling-top-edge", async () =>
    {
        var cases = new List<Sample>();
        foreach (var (label, width, height) in WindowSizes)
        {
            await using var scene = await RealChatScene.CreateAsync(width: width, height: height);
            for (var wheel = 2; wheel <= 10; wheel += 2)
            {
                var placement = Placement(scene, wheel);
                // 顶边附近: 视口里第一个按钮, 以及它后面那个。
                cases.AddRange(TogglePair(scene, $"{label} 顶边 wheel{wheel}", near: 0, placement));
                cases.AddRange(TogglePair(scene, $"{label} 顶边次条 wheel{wheel}", near: 1, placement));
            }
        }
        return cases;
    });

    /** 同一位置把视口里首/中/末三条可折叠消息都点一遍: 有些位置只有点某一条才跳。 */
    [Fact]
    public Task TogglingFirstMiddleAndLastMessageNearBottom() => Measure("first-middle-last-near-bottom", async () =>
    {
        var cases = new List<Sample>();
        foreach (var (label, width, height) in WindowSizes)
        {
            await using var scene = await RealChatScene.CreateAsync(width: width, height: height);
            foreach (var wheel in new[] { 2, 4, 8 })
            {
                var placement = Placement(scene, wheel);
                foreach (var (near, name) in new[] { (0, "first"), (1, "middle"), (2, "last") })
                    cases.AddRange(TogglePair(scene, $"{label} wheel{wheel} {name}", near, placement));
            }
        }
        return cases;
    });

    /** diff 卡片标题栏的 chevron(卡片本体展开/收起): 同样改卡片高度, 同样不该把别的消息顶走。 */
    [Fact]
    public Task TogglingDiffCardChevronsNearBottom() => Measure("diff-card-chevrons-near-bottom", async () =>
    {
        var cases = new List<Sample>();
        foreach (var (label, width, height) in WindowSizes)
        {
            await using var scene = await RealChatScene.CreateAsync(width: width, height: height);
            for (var wheel = 0; wheel <= 12; wheel++)
                cases.AddRange(TogglePair(scene, $"{label} card wheel{wheel}", near: 2,
                    Placement(scene, wheel), RealChatScene.FoldKind.Card));
        }
        return cases;
    });

    /** 展开之后继续滚动、再折叠: 用户连续操作时补偿是否还成立。 */
    [Fact]
    public Task ExpandingThenScrollingThenCollapsing() => Measure("expand-scroll-collapse", async () =>
    {
        var cases = new List<Sample>();
        await using var scene = await RealChatScene.CreateAsync();
        cases.AddRange(TogglePair(scene, "1280x800 wheel6", near: 2, Placement(scene, 6)));
        scene.Wheel(2);
        scene.Render();
        cases.AddRange(TogglePair(scene, "1280x800 wheel6 再滚一档", near: 2, Placement(scene, 0, scroll: false)));
        return cases;
    });

    /** 同一张卡片反复点: 每次的估算噪声会不会累积成越来越大的偏移。 */
    [Fact]
    public Task TogglingTheSameMessageRepeatedly() => Measure("same-message-repeatedly", async () =>
    {
        var cases = new List<Sample>();
        await using var scene = await RealChatScene.CreateAsync();
        var placement = Placement(scene, 4);
        for (var round = 0; round < 6; round++)
            cases.AddRange(TogglePair(scene, $"round {round}", near: 2, placement));
        return cases;
    });

    /** 以贴底为起点, 逐条往下把视口里能点的消息都点一遍(用户就是顺手一条条点过去)。 */
    [Fact]
    public Task ClickingEveryMessageInTurnNearBottom() => Measure("every-message-in-turn", async () =>
    {
        var cases = new List<Sample>();
        await using var scene = await RealChatScene.CreateAsync();
        Placement(scene, 5);
        for (var round = 0; round < 12; round++)
        {
            var count = scene.VisibleFoldButtons().Count;
            if (count == 0)
                break;
            cases.AddRange(TogglePair(scene, $"round {round} slot {round % count}/{count}", round % count, null));
        }
        return cases;
    });

    /**
     * 折叠之后继续流式输出: 会话末条消息在涨高。
     * 用户报的"跳了又被补偿拉回来"最容易出现在这种"折叠 + 内容增长"叠加的时刻。
     */
    [Fact]
    public Task TogglingWhileTheLastMessageStreams() => Measure("toggle-while-streaming", async () =>
    {
        var cases = new List<Sample>();
        await using var scene = await RealChatScene.CreateAsync();
        var streaming = scene.ViewModel.Messages[^2];
        if (!streaming.ShowBody)
        {
            streaming.IsExpanded = true;
            scene.Render();
        }
        var placement = Placement(scene, 5);
        var target = Pick(scene, 2, RealChatScene.FoldKind.Message);
        if (target is null)
            return cases;
        var sample = Click(scene, $"折叠后流式 [{placement}]", target.Value, RealChatScene.FoldKind.Message);
        cases.Add(sample);
        for (var chunk = 0; chunk < 4; chunk++)
        {
            streaming.Append($"\n流式新增 {chunk}: 这段会撑高末条消息。");
            streaming.FlushPending();
            scene.Render();
            cases.Add(Click(scene, $"流式 {chunk} 后再折叠", Pick(scene, 2, RealChatScene.FoldKind.Message)!.Value,
                RealChatScene.FoldKind.Message));
        }
        return cases;
    });

    // ---------------- 采样 ----------------

    /** 让视口停在"贴底后再上滚 wheel 档"的位置; scroll=false 表示沿用当前位置。 */
    private static string? Placement(RealChatScene scene, int wheel, bool scroll = true, bool keepSticky = false)
    {
        if (!scroll)
            return $"gap {scene.MaximumOffset - scene.Offset:0}";
        scene.ScrollToFraction(1);
        scene.Settle();
        for (var step = 0; step < wheel; step++)
        {
            scene.Wheel(3);
            scene.Render();
        }
        // 真实滚轮会把跟随意图改成"不在底部"; 想测贴底态就把它放回底部但保持跟随。
        if (keepSticky && wheel == 0)
        {
            scene.ScrollToFraction(1);
            scene.Settle();
        }
        return $"gap {scene.MaximumOffset - scene.Offset:0}/{scene.Extent:0}";
    }

    /** 在同一位置上点开第 near 个折叠控件(0=首, 1=中, 2=末), 再点回去; 两次点击各成一个样本。 */
    private static List<Sample> TogglePair(RealChatScene scene, string label, int near, string? placement,
        RealChatScene.FoldKind kind = RealChatScene.FoldKind.Message)
    {
        var samples = new List<Sample>();
        var target = Pick(scene, near, kind);
        if (target is null)
            return samples;
        var caseLabel = $"{label} [{placement ?? $"{scene.MaximumOffset - scene.Offset:0}"}]";
        samples.Add(Click(scene, caseLabel, target.Value, kind));
        var back = scene.EnsureFoldButtonVisible(target.Value.Message, kind);
        if (back is not null)
            samples.Add(Click(scene, caseLabel + " (收回)", back.Value, kind));
        return samples;
    }

    private static RealChatScene.FoldTarget? Pick(RealChatScene scene, int near, RealChatScene.FoldKind kind)
    {
        var buttons = scene.VisibleFoldButtons(kind);
        if (buttons.Count == 0)
            return null;
        var index = near switch
        {
            0 => 0,
            1 => buttons.Count / 2,
            2 => buttons.Count - 1,
            _ => Math.Min(buttons.Count - 1, Math.Max(0, near)),
        };
        return buttons[index];
    }

    /** 点一次, 逐帧记录视口里每条消息的位移。 */
    private static Sample Click(RealChatScene scene, string label, RealChatScene.FoldTarget target,
        RealChatScene.FoldKind kind)
    {
        var message = target.Message;
        var expandedBefore = IsExpanded(scene, message, kind);
        var targetText = $"{message.Kind}/{message.Text.Length}";
        var fromEnd = scene.MessageCount - scene.ViewModel.Messages.IndexOf(message);
        var clickable = scene.HitsButton(target.Button, out var hitReason);
        // 点不到时先像用户那样把布局跑完、必要时把它滚回视口, 再判可点。
        // 只有"怎么等、怎么滚都点不到"才算真的点空了; 需要重试的样本单独记为"点击前视口未自洽"。
        var unsettled = false;
        for (var attempt = 0; attempt < 4 && !clickable; attempt++)
        {
            unsettled = true;
            scene.Pump(3);
            var retry = scene.EnsureFoldButtonVisible(message, kind) ?? scene.FoldButtonOf(message, kind);
            if (retry is null)
                continue;
            target = retry.Value;
            clickable = scene.HitsButton(target.Button, out hitReason);
        }

        var sticky = scene.IsSticky;
        var clickedBefore = target.ItemTop;
        var heightBefore = target.ItemHeight;
        var offsetBefore = scene.Offset;
        var extentBefore = scene.Extent;
        var before = scene.VisibleMessages();

        var recycled = false;
        var frameTops = new List<Dictionary<MessageViewModel, double>>();
        if (clickable)
        {
            try
            {
                scene.Tap(target.Button);
            }
            catch (InvalidOperationException)
            {
                recycled = true;
            }
        }
        else
        {
            // 点不到就不要点: 点在别的元素上会制造出与折叠无关的位移, 污染证据。
            scene.Pump(1);
        }

        double maxOffsetSwing = 0, maxExtentSwing = 0;
        for (var frame = 0; frame < ObservedFrames && !recycled; frame++)
        {
            scene.Pump(1);
            maxOffsetSwing = Math.Max(maxOffsetSwing, Math.Abs(scene.Offset - offsetBefore));
            maxExtentSwing = Math.Max(maxExtentSwing, Math.Abs(scene.Extent - extentBefore));
            frameTops.Add(scene.AllRealizedTops());
        }

        // 收敛性: 再多跑若干帧, 位置与 offset 都不许再变(否则就是补偿/重估在和自己打架, 视口持续漂)。
        var settledTop = scene.TopOf(message);
        var settledOffset = scene.Offset;
        var settledExtent = scene.Extent;
        var unsettledAfter = false;
        for (var frame = 0; frame < ObservedFrames && !recycled; frame++)
        {
            scene.Pump(1);
            if (scene.TopOf(message) is not { } now)
                break;
            if (settledTop is { } prior && Math.Abs(now - prior) > PositionTolerance)
                unsettledAfter = true;
            if (Math.Abs(scene.Offset - settledOffset) > PositionTolerance
                || Math.Abs(scene.Extent - settledExtent) > PositionTolerance)
                unsettledAfter = true;
        }

        var containerAfter = scene.Container(message);
        recycled |= containerAfter is null;
        var heightDelta = containerAfter is null ? 0 : containerAfter.Bounds.Height - heightBefore;
        var expandedAfter = recycled ? expandedBefore : IsExpanded(scene, message, kind);
        var clickedDrift = containerAfter is null || scene.TopOf(message) is not { } after
            ? double.NaN
            : after - clickedBefore;

        var expectedShift = heightDelta;
        var worstOther = "无";
        var maxOtherResidual = 0d;
        var maxTransientResidual = 0d;
        foreach (var (other, topBefore) in before)
        {
            if (ReferenceEquals(other, message))
                continue;
            var expected = topBefore > clickedBefore + PositionTolerance ? expectedShift : 0;
            foreach (var frame in frameTops)
                if (frame.TryGetValue(other, out var topNow))
                    maxTransientResidual = Math.Max(maxTransientResidual, Math.Abs(topNow - topBefore - expected));
            if (scene.TopOf(other) is not { } topAfter)
                continue;
            var finalResidual = Math.Abs(topAfter - topBefore - expected);
            if (finalResidual <= maxOtherResidual)
                continue;
            maxOtherResidual = finalResidual;
            worstOther = $"{other.Kind}/{other.Text.Length} {topBefore:0}→{topAfter:0} 预期{expected:+0;-0;0}";
        }

        return new Sample(label, kind == RealChatScene.FoldKind.Card ? "卡片" : "消息", targetText, fromEnd,
            expandedBefore, expandedAfter, clickedDrift, heightDelta, maxOtherResidual, maxTransientResidual,
            worstOther, offsetBefore, scene.Offset, maxOffsetSwing, extentBefore, scene.Extent, maxExtentSwing)
        {
            Clickable = clickable,
            HitReason = hitReason,
            Sticky = sticky,
            Recycled = recycled,
            Unsettled = unsettled,
            DriftsAfterSettle = unsettledAfter,
        };
    }

    /** 当前折叠态: 消息看 IsExpanded, 卡片看 DiffView.IsCardExpanded(卡片自己才是展开态的所有者)。 */
    private static bool IsExpanded(RealChatScene scene, MessageViewModel message, RealChatScene.FoldKind kind)
        => kind == RealChatScene.FoldKind.Card
            ? scene.IsCardExpanded(message) ?? message.IsDiffExpanded
            : message.IsExpanded;

    /**
     * 跑一轮采样并写证据文件。断言只校验"场景可用"(读到真实会话、真的产生了可点样本、真的翻转了),
     * 具体阈值留给修复后转成断言。
     */
    private static Task Measure(string name, Func<Task<List<Sample>>> run)
        => HeadlessGui.RunAsync(async () =>
        {
            var cases = await run();
            var report = new StringBuilder();
            var summary = cases.SelectMany(sample => sample.Symptoms())
                .GroupBy(symptom => symptom)
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Key}={group.Count()}")
                .ToList();
            report.AppendLine($"# {name}");
            report.AppendLine($"样本 {cases.Count}, 有症状 {cases.Count(sample => sample.Fails)} | "
                + $"症状分布: {string.Join(", ", summary.DefaultIfEmpty("无"))}");
            report.AppendLine("分组: " + string.Join(" | ", cases
                .GroupBy(sample => (sample.Kind, sample.Sticky))
                .OrderBy(group => group.Key.ToString())
                .Select(group =>
                    $"{group.Key.Kind}/{(group.Key.Sticky ? "贴底" : "中途")} {group.Count()} 个, "
                    + $"有症状 {group.Count(sample => sample.Fails)}")));
            report.AppendLine();
            foreach (var sample in cases.OrderBy(sample => sample.Fails ? 0 : 1))
                report.AppendLine(sample.Describe());
            File.WriteAllText(Path.Combine(OutputDirectory, $"{name}.txt"), report.ToString());

            Assert.True(cases.Count > 0, $"{name}: 没有采到任何样本, 场景本身失效");
            Assert.Contains(cases, sample => sample.Flipped);
        }, MatrixTimeout);
}
