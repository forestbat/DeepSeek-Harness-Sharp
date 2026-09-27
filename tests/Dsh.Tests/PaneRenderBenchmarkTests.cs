using System.Diagnostics;
using System.Text;
using Dsh.Tui;
using OpenTK.Graphics.OpenGL;

namespace Dsh.Tests;

/**
 * 2x2 四 pane 渲染压测: 四个 pane 各自高速流入 transcript 增量, 分别测 CPU(AnsiRenderer)与 GPU(GpuRenderCore)后端。
 * GPU 项与 RenderPipelineBenchmarkTests 同属重载类, 可用 --filter "Category!=GpuStress" 排除。
 */
[Collection("RenderBench")]
[Trait("Category", "RenderBench")]
public class PaneRenderBenchmarkTests
{
    private const int AreaWidth = 240;
    private const int AreaHeight = 67;
    private const int CorpusLines = 4000;
    private const int Seed = 20260925;
    private const int RowsPerPanePerFrame = 3;
    private const int WarmupFrames = 40;
    private const int Frames = 600;
    /** 下限取保守值: 60 fps(16.7 ms/帧)是终端流畅线, 这里要求至少 2 倍余量, 避免 CI 抖动误报。 */
    private const double MinimumFps = 30;

    private static readonly PaneNode TwoByTwo = new PaneSplit(
        SplitOrientation.Vertical,
        0.5,
        new PaneSplit(SplitOrientation.Horizontal, 0.5, new PaneLeaf(0), new PaneLeaf(1)),
        new PaneSplit(SplitOrientation.Horizontal, 0.5, new PaneLeaf(2), new PaneLeaf(3)));

    private sealed class PaneStream
    {
        public required ConsoleRect Rect { get; init; }

        public required Cell[][] Lines { get; init; }

        public int Offset { get; set; }
    }

    [Fact]
    public void Cpu_Four_Pane_Frame_Benchmark()
    {
        var streams = Prepare(out var composite);
        var renderer = new AnsiRenderer();
        for (var frame = 0; frame < WarmupFrames; frame++)
        {
            Advance(composite, streams);
            renderer.Render(composite, 0, 0);
        }

        var samples = new long[Frames];
        for (var frame = 0; frame < Frames; frame++)
        {
            Advance(composite, streams);
            var started = Stopwatch.GetTimestamp();
            _ = renderer.Render(composite, 0, 0);
            samples[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        }

        var report = Header("CPU(AnsiRenderer, 合成单张全幅格栅后整帧输出)");
        AppendStats(report, "CPU 四 pane", samples);
        BenchReport.Write("pane-2x2-cpu-benchmark.md", report);

        var fps = Fps(samples);
        Assert.True(fps >= MinimumFps, $"CPU 四 pane 帧率 {fps:F1} fps 低于下限 {MinimumFps} fps");
    }

    [Fact]
    [Trait("Category", "GpuStress")]
    public void Gpu_Four_Pane_Frame_Benchmark()
    {
        var streams = Prepare(out var composite);
        var atlas = GlyphAtlas.Shared;
        using var egl = HeadlessGl.Create(
            AreaWidth * GlyphAtlas.Shared.GlyphWidth,
            AreaHeight * GlyphAtlas.Shared.GlyphHeight);
        atlas.Prewarm();
        using var core = new GpuRenderCore();
        core.Initialize(atlas);
        core.EnsureCellCapacity(AreaWidth * AreaHeight);
        GL.Viewport(0, 0, AreaWidth * GlyphAtlas.Shared.GlyphWidth, AreaHeight * GlyphAtlas.Shared.GlyphHeight);

        var previous = new CellGrid(AreaWidth, AreaHeight);
        var packed = new uint[AreaWidth * AreaHeight];
        var ranges = new List<(int Start, int Count)>();
        for (var frame = 0; frame < WarmupFrames; frame++)
        {
            Advance(composite, streams);
            DrawFrame(core, atlas, composite, previous, packed, ranges);
        }

        using var stats = GpuStatQueries.TryCreate();
        var build = new long[Frames];
        var upload = new long[Frames];
        var rasterFull = new long[Frames];
        var gpuNsFull = new long[Frames];
        for (var frame = 0; frame < Frames; frame++)
        {
            Advance(composite, streams);
            var started = Stopwatch.GetTimestamp();
            CellPacker.CollectDirtyRowRanges(composite, previous, ranges);
            foreach (var (start, count) in ranges)
                CellPacker.PackRows(composite, start, count, packed, atlas);
            build[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
            composite.CopyTo(previous);

            started = Stopwatch.GetTimestamp();
            foreach (var (start, count) in ranges)
                core.UploadCells(packed, start * AreaWidth, count * AreaWidth);
            upload[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;

            started = Stopwatch.GetTimestamp();
            stats?.Begin();
            core.RenderFrame(atlas, AreaWidth, AreaHeight);
            stats?.End();
            GL.Finish();
            rasterFull[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
            stats?.Collect();
            gpuNsFull[frame] = stats is null ? 0 : (long)stats.ElapsedNs;
        }

        var report = Header(
            "GPU(GpuRenderCore: 单个全幅格栅, 每帧只上传脏行, 光栅化恒整帧重绘)",
            $"GL 环境: {GL.GetString(StringName.Renderer)}, {GL.GetString(StringName.Version)}。"
            + $"GPU 执行时间: {(stats?.ElapsedNs > 0 ? "GL_TIME_ELAPSED" : "不可用")}。");
        AppendRow(report, "构建(脏行 diff + 打包)", build);
        AppendRow(report, "上传(UploadCells 脏行)", upload);
        AppendRow(report, "光栅化-整帧重绘(RenderFrame 全幅 + GL.Finish)", rasterFull);
        if (stats?.ElapsedNs > 0)
            AppendRow(report, "GPU 执行(仅 GL_TIME_ELAPSED)", gpuNsFull);
        var total = build.Zip(upload, (a, b) => a + b).Zip(rasterFull, (a, b) => a + b).ToArray();
        AppendRow(report, "三项合计", total);
        BenchReport.Write("pane-2x2-gpu-benchmark.md", report);

        var fps = Fps(rasterFull);
        Assert.True(fps >= MinimumFps, $"GPU 四 pane 光栅化帧率 {fps:F1} fps 低于下限 {MinimumFps} fps");
    }

    private static IReadOnlyList<PaneStream> Prepare(out CellGrid composite)
    {
        var layout = LayoutEngine.EvaluatePanes(TwoByTwo, new ConsoleRect(0, 0, AreaWidth, AreaHeight));
        Assert.Equal(4, layout.Panes.Count);
        Assert.All(layout.Panes, pane => Assert.True(pane.Rect.Width > 0 && pane.Rect.Height > 0));
        var streams = BuildStreams(layout);
        composite = new CellGrid(AreaWidth, AreaHeight);
        foreach (var stream in streams)
        {
            for (var row = 0; row < stream.Rect.Height; row++)
                AppendLine(composite, stream);
        }
        return streams;
    }

    private static IReadOnlyList<PaneStream> BuildStreams(PaneLayout layout)
        => [.. layout.Panes.Select(pane => new PaneStream
        {
            Rect = pane.Rect,
            Lines = MarkdownCorpus.Build(CorpusLines, Math.Max(1, pane.Rect.Width), Seed + pane.PaneId),
        })];

    /** 每帧给每个 pane 追加若干行: 滚动式覆盖, 只动该 pane 的少数行(增量而非全量重绘)。 */
    private static void Advance(CellGrid grid, IReadOnlyList<PaneStream> streams)
    {
        foreach (var stream in streams)
        {
            for (var step = 0; step < RowsPerPanePerFrame; step++)
                AppendLine(grid, stream);
        }
    }

    private static void AppendLine(CellGrid grid, PaneStream stream)
    {
        var line = stream.Lines[stream.Offset % stream.Lines.Length];
        var y = stream.Rect.Y + stream.Offset % stream.Rect.Height;
        var width = Math.Min(stream.Rect.Width, line.Length);
        for (var x = 0; x < width; x++)
            grid[stream.Rect.X + x, y] = line[x];
        stream.Offset++;
    }

    private static void DrawFrame(
        GpuRenderCore core, GlyphAtlas atlas, CellGrid grid, CellGrid previous, uint[] packed,
        List<(int Start, int Count)> ranges)
    {
        CellPacker.CollectDirtyRowRanges(grid, previous, ranges);
        foreach (var (start, count) in ranges)
            CellPacker.PackRows(grid, start, count, packed, atlas);
        grid.CopyTo(previous);
        foreach (var (start, count) in ranges)
            core.UploadCells(packed, start * AreaWidth, count * AreaWidth);
        core.RenderFrame(atlas, AreaWidth, AreaHeight);
        GL.Finish();
    }

    private static StringBuilder Header(string backend, string? environment = null)
    {
        var report = new StringBuilder();
        report.AppendLine($"# 2x2 四 pane 渲染压测: {backend}");
        report.AppendLine();
        report.AppendLine($"区域 {AreaWidth}x{AreaHeight} 格, 四等分后每 pane 约 {(AreaWidth - 1) / 2}x{(AreaHeight - 1) / 2} 格; "
            + $"每帧每 pane 流入 {RowsPerPanePerFrame} 行新 transcript, 预热 {WarmupFrames} 帧后测 {Frames} 帧。语料: 合成 markdown。");
        report.AppendLine();
        if (environment is not null)
        {
            report.AppendLine(environment);
            report.AppendLine();
        }
        report.AppendLine("| 项 | 均帧 | P95 | 帧率 | 下限 |");
        report.AppendLine("| --- | --- | --- | --- | --- |");
        return report;
    }

    private static void AppendStats(StringBuilder report, string label, long[] samples)
    {
        AppendRow(report, label, samples);
        report.AppendLine();
        report.AppendLine($"断言下限: {MinimumFps} fps(每帧 {1000.0 / MinimumFps:F1} ms)。");
    }

    private static void AppendRow(StringBuilder report, string label, long[] samples)
        => report.AppendLine($"| {label} | {samples.Average() / 1e6:F3} ms | {P95(samples) / 1e6:F3} ms | {Fps(samples):F1} fps | {MinimumFps} fps |");

    private static double Fps(long[] samples) => 1_000_000_000.0 / samples.Average();

    private static double P95(long[] samples)
    {
        var sorted = samples.Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
    }
}
