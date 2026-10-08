using System.Diagnostics;
using System.Text;
using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Tui;

namespace Dsh.Tests;

[Collection("RenderBench")]
[Trait("Category", "RenderBench")]
public class TranscriptBenchmarkTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(Path.GetTempPath(), $"dsh-transcript-bench-{Guid.NewGuid():N}");

    [Fact]
    public async Task Incremental_Wrap_Cache_Matches_Full_Rebuild()
    {
        var (chat, agent) = await CreateChat();
        const int width = 480;
        const int height = 135;

        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.BlockStart(0, "reasoning")));
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.ReasoningDelta(0, "先分析请求,确认渲染管线的瓶颈所在。\n需要覆盖 markdown 图片与像素画两种形态。")));
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.BlockEnd(0, new ReasoningBlock("先分析请求,确认渲染管线的瓶颈所在。\n需要覆盖 markdown 图片与像素画两种形态。"))));
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "## 结论\n**GPU 路径**在 4K 下端到端约 5 倍速。")));
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "这一行跨两个 chunk")));
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "才完整,用于覆盖部分行尾。\n")));
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "![截图](artifacts/gpu.png)\n")));
        var callId = ToolCallId.Create("call-1");
        var callSeq = agent.Session.Seq;
        agent.Session.Append(new ToolCallPayload(1, 1, callId, "bash", """{"command":"dotnet test --filter RenderBenchmark"}"""));
        agent.Session.Append(new ToolResultPayload(1, 1, MessageFactory.CreateToolResultMessage(callId, [new TextBlock("passed: 572, failed: 0\n耗时 21 m 31 s")], false)), new SurfaceOp.Append(), [callSeq]);
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "继续输出:折叠块之后的新行。\n末行无换行")));
        agent.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        chat.DrainUi();
        var incremental1 = DrawFrameAt(chat, width, height);
        _ = DrawFrameAt(chat, width - 1, height);
        var full1 = DrawFrameAt(chat, width, height);
        Assert.Equal(full1, incremental1);

        Press(chat, ConsoleKey.Tab);
        Press(chat, ConsoleKey.Enter);
        chat.DrainUi();
        agent.Session.Append(new TurnStartPayload(2));
        agent.Session.Append(new AssistantChunkPayload(2, 1, new StreamChunk.TextDelta(0, "折叠后继续流式的一行。\n")));
        agent.Session.Append(new TurnEndPayload(2, new TurnEndReason.Completed()));
        chat.DrainUi();
        var incremental2 = DrawFrameAt(chat, width, height);
        _ = DrawFrameAt(chat, width - 1, height);
        var full2 = DrawFrameAt(chat, width, height);
        Assert.Equal(full2, incremental2);
    }

    [Fact]
    public async Task Transcript_Streaming_Draw_Benchmark()
    {
        const int width = 480;
        const int height = 135;
        const int frames = 1000;
        const int linesPerFrame = 12;
        var textLines = MarkdownCorpus.BuildTextLines(frames * linesPerFrame + 64, 20260916);

        var (chat, agent) = await CreateChat();
        var layout = LayoutEngine.Calculate(width, height);
        var grid = new CellGrid(width, height);

        chat.Draw(grid, layout);
        var drawNanos = new long[frames];
        var drainNanos = new long[frames];
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < frames; frame++)
        {
            var started = Stopwatch.GetTimestamp();
            for (var offset = 0; offset < linesPerFrame; offset++)
                agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, $"{textLines[frame * linesPerFrame + offset]}\n")));
            chat.DrainUi();
            drainNanos[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;

            started = Stopwatch.GetTimestamp();
            chat.Draw(grid, layout);
            drawNanos[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        }
        var allocBytes = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        var report = new StringBuilder();
        report.AppendLine("# transcript 层流式压测(ChatWindow.Draw)");
        report.AppendLine();
        report.AppendLine($"网格 {width}x{height}(4K 档),{frames} 帧,每帧经 Session 事件总线注入 {linesPerFrame} 行 markdown 文本(AssistantChunk TextDelta),DrainUi 后 Draw。transcript 最终 {frames * linesPerFrame} 行。增量 wrap 缓存:仅新追加的完整行与生长中的尾行参与重建。");
        report.AppendLine();
        report.AppendLine("| 帧区间 | transcript 行数 | DrainUi 均帧 | Draw 均帧 | Draw P95 |");
        report.AppendLine("| --- | --- | --- | --- | --- |");
        const int bucket = 50;
        for (var start = 0; start < frames; start += bucket)
        {
            var drainSlice = drainNanos[start..(start + bucket)];
            var drawSlice = drawNanos[start..(start + bucket)];
            report.AppendLine($"| {start + 1}-{start + bucket} | ~{(start + bucket) * linesPerFrame} | {drainSlice.Average() / 1e6:F3} ms | {drawSlice.Average() / 1e6:F3} ms | {Pctl(drawSlice, 0.95) / 1e6:F3} ms |");
        }
        report.AppendLine();
        report.AppendLine($"全程 GC 分配: {allocBytes / 1e6:F1} MB。");

        var directory = Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/bench");
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, "render-transcript-benchmark.md"));
        File.WriteAllText(path, report.ToString());
        Assert.True(File.Exists(path));
    }

    private async Task<(ChatWindow Chat, AgentLoopAgent Agent)> CreateChat()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var commands = CommandsService.Register(ctx);
        commands.Register(new CommandDefinition { Name = "model", Description = "List or switch model", Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()) });
        commands.Register(new CommandDefinition { Name = "session", Description = "Manage sessions", Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()) });
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("deepseek-official", "deepseek-v4-flash")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        var home = HarnessHome.Resolve(_homeDir);
        return (new ChatWindow(ctx, agent, home), agent);
    }

    private static string DrawFrameAt(ChatWindow chat, int width, int height)
    {
        var layout = LayoutEngine.Calculate(width, height);
        var grid = new CellGrid(width, height);
        chat.Draw(grid, layout);
        var builder = new StringBuilder();
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
                builder.Append(grid[x, y].Character);
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static void Press(ChatWindow chat, ConsoleKey key)
        => chat.HandleKey(new ConsoleKeyInfo('\0', key, false, false, false));

    private static double Pctl(long[] values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * p) - 1];
    }

    public void Dispose() => TempTree.Delete(_homeDir);
}
