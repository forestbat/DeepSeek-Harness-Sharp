using System.Diagnostics;
using System.Text;
using Dsh.Tui;
using OpenTK.Graphics.OpenGL;

namespace Dsh.Tests;

/**
 * 渲染管线全档压测(1080p→5K 各 1000 帧)。
 * 已知限制: AMD 核显 + Windows Mesa/D3D12 栈在持续高负载下可能触发驱动 TDR(超时检测恢复)并连带崩掉 testhost——
 * 这是 AMD 驱动的既有问题, 不是本项目代码缺陷; 在该平台上撞到 TDR 时重跑即可, 或用 --filter "Category!=GpuStress" 把本类排除。
 */
[Collection("RenderBench")]
[Trait("Category", "RenderBench")]
public class RenderPipelineBenchmarkTests
{
    private const int CorpusLines = 4000;
    private const int SparseRowsPerFrame = 6;
    private const int Seed = 20260916;
    /** 格像素尺寸跟随图集默认值(字号默认档), 使各档 viewport 与真实渲染一致。 */
    private const int CellPixelWidth = GlyphAtlas.DefaultGlyphWidth;
    private const int CellPixelHeight = GlyphAtlas.DefaultGlyphHeight;

    private static readonly (int Width, int Height, string Label, int Frames)[] Tiers =
    [
        (240, 67, "240x67 格", 1000),
        (320, 90, "320x90 格", 1000),
        (480, 135, "480x135 格", 1000),
        (640, 180, "640x180 格", 1000),
    ];

    [Fact]
    [Trait("Category", "GpuStress")]
    public void Render_Pipeline_Benchmark()
    {
        using var egl = HeadlessGl.Create(Tiers[^1].Width * CellPixelWidth, Tiers[^1].Height * CellPixelHeight);
        var atlas = GlyphAtlas.Shared;
        atlas.Prewarm();
        var core = new GpuRenderCore();
        core.Initialize(atlas);

        var lines = MarkdownCorpus.Build(CorpusLines, Tiers[^1].Width, Seed);

        var stats = GpuStatQueries.TryCreate();
        var report = new StringBuilder();
        report.AppendLine("# 渲染管线四阶段压测(帧数据构建 / GL 上传 / GPU 光栅化 / 终端解析)");
        report.AppendLine();
        report.AppendLine($"GL 环境: {GL.GetString(StringName.Renderer)}, {GL.GetString(StringName.Version)}。光栅化用 GL.Finish 收束。GPU 计数器: {(stats is null ? "GPU 计时不可用" : stats.SupportsStats ? "ARB_pipeline_statistics + GL_TIME_ELAPSED(query 读数在 Finish 后取回)" : "仅 GL_TIME_ELAPSED(core 能力;ARB_pipeline_statistics 不支持)")}。MAX_TEXTURE_BUFFER_SIZE: {GL.GetInteger(GetPName.MaxTextureBufferSize)}。");
        report.AppendLine("阶段划分(每帧分别计时): 构建 = 脏行 diff + CellPacker.PackRows(打包与字形齐备检查融合单趟);上传 = GpuRenderCore.UploadCells(脏行切片);光栅化 = GpuRenderCore.RenderFrame + GL.Finish(含 GPU 排队执行);GPU 时间 = 同一 RenderFrame 的 GL_TIME_ELAPSED query(纯 GPU 执行,排除 CPU 提交开销)。语料: 合成 markdown。终端解析见 render-terminal-benchmark.md。");
        report.AppendLine($"EGL 设备: {string.Join(" ; ", egl.EnumeratedDevices)}。");
        WriteReport("render-pipeline-benchmark.md", report);
        report.AppendLine();

        foreach (var (width, height, label, frames) in Tiers)
        {
            GL.Viewport(0, 0, width * CellPixelWidth, height * CellPixelHeight);

            var grid = new CellGrid(width, height);
            var previous = new CellGrid(width, height);
            var packed = new uint[width * height];
            var ranges = new List<(int Start, int Count)>();
            core.EnsureCellCapacity(width * height);

            for (var frame = 0; frame < 40; frame++)
            {
                FillStreaming(lines, grid, frame);
                CellPacker.PackRows(grid, 0, height, packed, atlas);
                core.UploadCells(packed, 0, width * height);
                core.RenderFrame(atlas, width, height);
            }
            GL.Finish();

            report.AppendLine($"## {label}: {width}x{height} = {width * height} 格,{frames} 帧/场景");
            report.AppendLine();
            report.AppendLine("| 场景 | 构建均帧 | 上传均帧 | 光栅化均帧 | GPU 时间 | 三项合计 | 上传 P95 | GPU 时间 P95 |");
            report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var streaming in new[] { true, false })
            {
                var m = MeasurePipeline(core, atlas, grid, previous, lines, packed, ranges, width, height, frames, streaming, stats);
                var total = Avg(m.Build) + Avg(m.Upload) + Avg(m.Raster);
                report.AppendLine($"| {(streaming ? "A 全量流式" : "B 稀疏更新")} | {Avg(m.Build) / 1e6:F3} ms | {Avg(m.Upload) / 1e6:F3} ms | {Avg(m.Raster) / 1e6:F3} ms | {Avg(m.GpuNs) / 1e6:F3} ms | {total / 1e6:F3} ms | {Pctl(m.Upload, 0.95) / 1e6:F3} ms | {Pctl(m.GpuNs, 0.95) / 1e6:F3} ms |");
                if (stats is not null)
                    report.AppendLine($"| {(streaming ? "A" : "B")} 计数器 | VS 调用 {m.VsInvocations.Average():N0} | 顶点提交 {m.Vertices.Average():N0} | 图元提交 {m.Primitives.Average():N0} | 图元生成 {m.PrimitivesGenerated.Average():N0} | | | |");
            }
            if (width == 480)
            {
                var pixelLines = RenderBenchmarkTests.BuildPixelLines(CorpusLines, width, Seed);
                FillStreaming(pixelLines, grid, 0);
                CellPacker.PackRows(grid, 0, height, packed, atlas);
                core.UploadCells(packed, 0, width * height);
                core.RenderFrame(atlas, width, height);
                GL.Finish();
                var m = MeasurePipeline(core, atlas, grid, previous, pixelLines, packed, ranges, width, height, 1000, streaming: true, stats);
                var total = Avg(m.Build) + Avg(m.Upload) + Avg(m.Raster);
                report.AppendLine($"| C 像素画(1000 帧) | {Avg(m.Build) / 1e6:F3} ms | {Avg(m.Upload) / 1e6:F3} ms | {Avg(m.Raster) / 1e6:F3} ms | {Avg(m.GpuNs) / 1e6:F3} ms | {total / 1e6:F3} ms | {Pctl(m.Upload, 0.95) / 1e6:F3} ms | {Pctl(m.GpuNs, 0.95) / 1e6:F3} ms |");
                if (stats is not null)
                    report.AppendLine($"| C 计数器 | VS 调用 {m.VsInvocations.Average():N0} | 顶点提交 {m.Vertices.Average():N0} | 图元提交 {m.Primitives.Average():N0} | 图元生成 {m.PrimitivesGenerated.Average():N0} | | | |");
            }
            report.AppendLine();
            WriteReport("render-pipeline-benchmark.md", report);
        }

        stats?.Dispose();
        core.Dispose();
        WriteReport("render-pipeline-benchmark.md", report);
    }

    private sealed record PipelineMeasurement(long[] Build, long[] Upload, long[] Raster, long[] GpuNs, double[] Vertices, double[] Primitives, double[] VsInvocations, double[] PrimitivesGenerated);

    private static PipelineMeasurement MeasurePipeline(
        GpuRenderCore core, GlyphAtlas atlas, CellGrid grid, CellGrid previous, Cell[][] lines,
        uint[] packed, List<(int Start, int Count)> ranges, int width, int height, int frames, bool streaming,
        GpuStatQueries? stats)
    {
        var build = new long[frames];
        var upload = new long[frames];
        var raster = new long[frames];
        var gpuNs = new long[frames];
        var vertices = new double[frames];
        var primitives = new double[frames];
        var vsInvocations = new double[frames];
        var primitivesGenerated = new double[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            Fill(lines, grid, frame, streaming);
            var started = Stopwatch.GetTimestamp();
            CellPacker.CollectDirtyRowRanges(grid, previous, ranges);
            foreach (var (start, count) in ranges)
                CellPacker.PackRows(grid, start, count, packed, atlas);
            build[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
            grid.CopyTo(previous);

            started = Stopwatch.GetTimestamp();
            foreach (var (start, count) in ranges)
                core.UploadCells(packed, start * width, count * width);
            upload[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;

            started = Stopwatch.GetTimestamp();
            stats?.Begin();
            core.RenderFrame(atlas, width, height);
            stats?.End();
            GL.Finish();
            raster[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
            stats?.Collect();
            gpuNs[frame] = stats is null ? 0 : (long)stats.ElapsedNs;
            if (stats is not null)
            {
                vertices[frame] = stats.Vertices;
                primitives[frame] = stats.Primitives;
                vsInvocations[frame] = stats.VsInvocations;
                primitivesGenerated[frame] = stats.PrimitivesGenerated;
            }
        }
        return new PipelineMeasurement(build, upload, raster, gpuNs, vertices, primitives, vsInvocations, primitivesGenerated);
    }

    [Fact]
    public void Terminal_Parse_Benchmark()
    {
        var tmux = FindOnPath("tmux");
        var report = new StringBuilder();
        report.AppendLine("# 终端解析压测(CPU ANSI 输出的消费侧)");
        report.AppendLine();
        if (tmux is null)
        {
            report.AppendLine("跳过: 未找到 tmux。");
            WriteReport("render-terminal-benchmark.md", report);
            return;
        }

        const int width = 480;
        const int height = 135;
        var markdownLines = MarkdownCorpus.Build(CorpusLines, width, Seed);
        var pixelLines = RenderBenchmarkTests.BuildPixelLines(CorpusLines, width, Seed);
        var session = $"dshbench{Environment.ProcessId}";
        try
        {
            Run(tmux, $"new-session -d -s {session} -x {width} -y {height}");
            MeasureStream(tmux, session, report, "markdown 场景 A", markdownLines, width, height, frames: 1000, "terminal-stream.txt");
            MeasureStream(tmux, session, report, "像素画场景 C(每格异色)", pixelLines, width, height, frames: 1000, "terminal-stream-pixel.txt");
            report.AppendLine("口径说明: 计时从写入 pane tty 开始到 tmux 解析出哨兵为止,含 tmux 的解析+屏幕更新+回滚;这是终端侧成本的参考值,GPU 加速终端(alacritty/kitty 等)会更快。哨兵探测轮询间隔 20 ms,对小流有量化误差。");
        }
        finally
        {
            Run(tmux, $"kill-session -t {session}");
        }
        WriteReport("render-terminal-benchmark.md", report);
    }

    private static void MeasureStream(string tmux, string session, StringBuilder report, string label, Cell[][] lines, int width, int height, int frames, string fileName)
    {
        var grid = new CellGrid(width, height);
        var renderer = new AnsiRenderer();
        var stream = new StringBuilder(frames * width * height);
        for (var frame = 0; frame < frames; frame++)
        {
            FillStreaming(lines, grid, frame);
            stream.Append(renderer.Render(grid, 0, 0));
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/bench");
        Directory.CreateDirectory(directory);
        var streamPath = Path.GetFullPath(Path.Combine(directory, fileName));
        File.WriteAllText(streamPath, stream.ToString());

        var paneTty = Run(tmux, $"list-panes -t {session} -F \"#{{pane_tty}}\"").Trim();
        var sentinel = $"__DSH_DONE_{fileName}__";
        using var feeder = Process.Start(new ProcessStartInfo("bash", $"-c \"cat '{streamPath}' > {paneTty}; printf '\\n{sentinel}\\n' > {paneTty}\"")
        {
            UseShellExecute = false,
        })!;
        var started = Stopwatch.GetTimestamp();
        var delivered = false;
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < 120)
        {
            var pane = Run(tmux, $"capture-pane -p -t {session} -S -");
            if (pane.Contains(sentinel, StringComparison.Ordinal))
            {
                delivered = true;
                break;
            }
            Thread.Sleep(20);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (!delivered && !feeder.HasExited)
            feeder.Kill();
        var megabytes = stream.Length * sizeof(char) / 1e6;
        report.AppendLine($"## {label}: {frames} 帧,共 {megabytes:F1} MB(UTF-16 计)");
        report.AppendLine();
        if (delivered)
            report.AppendLine($"整流消费耗时 {elapsed.TotalSeconds:F2} s,吞吐 {megabytes / elapsed.TotalSeconds:F2} MB/s,折合每帧 {elapsed.TotalMilliseconds / frames:F2} ms。");
        else
            report.AppendLine("超时(120 s)未完成消费,哨兵未出现。");
        report.AppendLine();
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string Run(string fileName, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static void Fill(Cell[][] lines, CellGrid grid, int frame, bool streaming)
    {
        if (streaming || frame == 0)
            FillStreaming(lines, grid, streaming ? frame : 0);
        else
            FillSparse(lines, grid, frame);
    }

    private static void FillStreaming(Cell[][] lines, CellGrid grid, int frame)
    {
        for (var row = 0; row < grid.Height; row++)
            grid.SetRow(row, lines[(frame + row) % lines.Length]);
    }

    private static void FillSparse(Cell[][] lines, CellGrid grid, int frame)
    {
        var start = frame * SparseRowsPerFrame;
        for (var offset = 0; offset < SparseRowsPerFrame; offset++)
        {
            var index = start + offset;
            grid.SetRow(index % grid.Height, lines[index % lines.Length]);
        }
    }

    private static double Avg(long[] values) => values.Average();

    private static double Pctl(long[] values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * p) - 1];
    }

    private static void WriteReport(string fileName, StringBuilder report) => BenchReport.Write(fileName, report);

}
