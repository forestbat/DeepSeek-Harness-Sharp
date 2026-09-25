using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Dsh.Tui;

namespace Dsh.Tests;

[Collection("RenderBench")]
[Trait("Category", "RenderBench")]
public class RenderBenchmarkTests
{
    private const int CorpusLines = 4000;
    private const int SparseRowsPerFrame = 6;
    private const int Seed = 20260916;

    private static readonly (int Width, int Height, string Label, int Frames)[] Tiers =
    [
        (240, 67, "240x67 格", 1000),
        (320, 90, "320x90 格", 1000),
        (480, 135, "480x135 格", 1000),
        (640, 180, "640x180 格", 1000),
    ];

    [Fact]
    public void Gpu_Vs_Cpu_Render_Benchmark()
    {
        var lines = MarkdownCorpus.Build(CorpusLines, Tiers[^1].Width, Seed);
        var report = new StringBuilder();
        report.AppendLine("# GPU vs CPU 渲染压测(markdown 语料)");
        report.AppendLine();
        report.AppendLine($"语料: {CorpusLines} 行合成 markdown(标题/粗体/行内代码/代码块/列表/表格/引用/JSON 工具结果/思维链,CJK 与 ASCII 混排,语法着色按真实 TUI 惯例)。");
        report.AppendLine("网格档位为数量级代号(构建侧基准不出像素);图集默认 13pt = 10x16 px/格(对齐 Rider 终端)。填充走 CellGrid.SetRow(span 拷贝,不计入被测路径)。");
        report.AppendLine("CPU 路径: AnsiRenderer.Render(行级 diff);GPU 路径: 脏行 diff + CellPacker.PackRows(打包与字形齐备检查融合单趟)。两者均为帧数据构建,不含 GL 上传/绘制与终端消费。");
        report.AppendLine();

        foreach (var (width, height, label, frames) in Tiers)
        {
            var grid = new CellGrid(width, height);
            var previous = new CellGrid(width, height);
            var renderer = new AnsiRenderer();
            var packed = new uint[width * height];
            var ranges = new List<(int Start, int Count)>();

            FillStreaming(lines, grid, 0);
            renderer.Render(grid, 0, 0);
            CellPacker.PackRows(grid, 0, height, packed, GlyphAtlas.Shared);

            var streaming = Measure(renderer, grid, previous, lines, packed, ranges, frames, streaming: true);
            var sparse = Measure(renderer, grid, previous, lines, packed, ranges, frames, streaming: false);

            var pixelLines = BuildPixelLines(CorpusLines, width, Seed);
            FillStreaming(pixelLines, grid, 0);
            renderer.Render(grid, 0, 0);
            CellPacker.PackRows(grid, 0, height, packed, GlyphAtlas.Shared);
            var pixel = Measure(renderer, grid, previous, pixelLines, packed, ranges, frames, streaming: true);

            var wideLines = BuildWideLines(CorpusLines, width, Seed);
            FillStreaming(wideLines, grid, 0);
            renderer.Render(grid, 0, 0);
            // 预热: 先把语料字形烘进图集(真实 TUI 只在启动后首批 CJK 文本付一次性烘焙), 计时测的是稳态构建而非一次性光栅化摊销。
            CellPacker.PackRows(grid, 0, height, packed, GlyphAtlas.Shared);
            var wide = Measure(renderer, grid, previous, wideLines, packed, ranges, frames, streaming: true);

            report.AppendLine($"## {label}: {width}x{height} = {width * height} 格,{frames} 帧");
            report.AppendLine();
            report.AppendLine("### 场景 A:全量流式(每帧整屏替换)");
            report.AppendLine();
            AppendTable(report, streaming, frames);
            report.AppendLine();
            report.AppendLine($"### 场景 B:稀疏更新(每帧仅 {SparseRowsPerFrame} 行变化)");
            report.AppendLine();
            AppendTable(report, sparse, frames);
            report.AppendLine();
            report.AppendLine("### 场景 C:像素画/内嵌图片(半块色块,每格异色)");
            report.AppendLine();
            report.AppendLine("模拟终端内嵌图片渲染(sixel/半块路径): 全屏 '▀' 半块字符,每格随机前景/背景色。背景 run 合并与 SGR run 完全失效, quad 数与 ANSI 体积均为上界。");
            report.AppendLine();
            AppendTable(report, pixel, frames);
            report.AppendLine();
            report.AppendLine("### 场景 D:满屏宽字符(每两格一个 CJK 宽字)");
            report.AppendLine();
            report.AppendLine("满屏互不相同的 CJK 宽字符(1500 字池,图集可容),模拟 CJK 会话/文档流。glyph 查找与 '\\0' 尾格路径顶满。");
            report.AppendLine();
            AppendTable(report, wide, frames);
            report.AppendLine();
        }

        WriteReport("render-benchmark.md", report);
    }

    [Fact]
    public void Gpu_WideChar_Focus_Benchmark()
    {
        var (width, height, label, _) = Tiers[2];
        const int frames = 6000;
        var lines = BuildWideLines(CorpusLines, width, Seed);
        var grid = new CellGrid(width, height);
        var previous = new CellGrid(width, height);
        var packed = new uint[width * height];
        var ranges = new List<(int Start, int Count)>();
        FillStreaming(lines, grid, 0);
        CellPacker.PackRows(grid, 0, height, packed, GlyphAtlas.Shared);
        Collect();
        var nanos = new long[frames];
        var diffNanos = new long[frames];
        var packNanos = new long[frames];
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < frames; frame++)
        {
            FillStreaming(lines, grid, frame);
            var started = Stopwatch.GetTimestamp();
            CellPacker.CollectDirtyRowRanges(grid, previous, ranges);
            var afterDiff = Stopwatch.GetTimestamp();
            foreach (var (start, count) in ranges)
                CellPacker.PackRows(grid, start, count, packed, GlyphAtlas.Shared);
            var afterPack = Stopwatch.GetTimestamp();
            nanos[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
            diffNanos[frame] = (long)Stopwatch.GetElapsedTime(started, afterDiff).TotalNanoseconds;
            packNanos[frame] = (long)Stopwatch.GetElapsedTime(afterDiff, afterPack).TotalNanoseconds;
            grid.CopyTo(previous);
        }
        var allocBytes = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        var report = new StringBuilder();
        report.AppendLine("# 场景 D GPU 构建聚焦(宽字回归基准,管线 v2 打包路径)");
        report.AppendLine();
        report.AppendLine($"网格 {width}x{height}({label}),{frames} 帧满屏宽字。");
        report.AppendLine($"脏行 diff+融合打包(含字形齐备检查) 均帧 {Avg(nanos) / 1e6:F3} ms,P95 {Pctl(nanos, 0.95) / 1e6:F3} ms,GC {allocBytes / 1e6:F1} MB。");
        report.AppendLine($"分项均值: 行 diff {Avg(diffNanos) / 1e6:F3} ms,融合打包 {Avg(packNanos) / 1e6:F3} ms。");
        WriteReport("render-widechar-benchmark.md", report);
    }

    [Fact]
    public void Row_Diff_Ablation_Benchmark()
    {
        var (width, height, label, frames) = Tiers[2];
        var lines = MarkdownCorpus.Build(CorpusLines, width, Seed);
        var current = new CellGrid(width, height);
        var previous = new CellGrid(width, height);
        FillStreaming(lines, current, 0);
        current.CopyTo(previous);
        for (var offset = 0; offset < SparseRowsPerFrame; offset++)
            current.SetRow(offset, lines[100 + offset]);

        var report = new StringBuilder();
        report.AppendLine("# 行 diff 消融实验(可优化点-2)");
        report.AppendLine();
        report.AppendLine($"网格: {width}x{height}({label}),每帧 {height} 行全量比较,{frames} 帧,仅 {SparseRowsPerFrame} 行不等(稀疏场景典型分布)。`Cell` 尺寸: {UnsafeSize()} 字节。");
        report.AppendLine("- V0 基线: 逐格 `grid[x, y] != previous[x, y]`(索引器 + record struct 相等)。");
        report.AppendLine("- V1 单用 SequenceEqual: `Row(y).SequenceEqual(Row(y))`(Cell 泛型相等路径)。");
        report.AppendLine("- V2 单用 MemoryMarshal: 行字节 span 转 `ReadOnlySpan<ulong>` 后每次比较 2 个 ulong(16 字节),尾部按字节。");
        report.AppendLine("- V3 联用: `MemoryMarshal.Cast<Cell, byte>` 后 `SequenceEqual<byte>`(JIT 向量化)。");
        report.AppendLine();
        report.AppendLine("| 变体 | 全帧行比较均帧 | P95 | 与 V0 判定一致 |");
        report.AppendLine("| --- | --- | --- | --- |");

        var baseline = RunVariant(report, "V0", current, previous, frames, RowEqualV0);
        RunVariant(report, "V1", current, previous, frames, RowEqualV1, baseline);
        RunVariant(report, "V2", current, previous, frames, RowEqualV2, baseline);
        RunVariant(report, "V3", current, previous, frames, RowEqualV3, baseline);
        report.AppendLine();
        report.AppendLine("注意: 若 `Cell` 含未初始化填充字节,字节级比较(V2/V3)可能把逻辑相等行判为不等(只会多渲染,不会少渲染);“判定一致”列以 V0 为准核对。");
        WriteReport("render-rowdiff-benchmark.md", report);
    }

    private static int UnsafeSize() => System.Runtime.CompilerServices.Unsafe.SizeOf<Cell>();

    private delegate bool RowComparer(CellGrid current, CellGrid previous, int y);

    private long RunVariant(StringBuilder report, string name, CellGrid current, CellGrid previous, int frames, RowComparer comparer, long expectedEqualRows = -1)
    {
        Collect();
        var nanos = new long[frames];
        long equalRows = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var started = Stopwatch.GetTimestamp();
            for (var y = 0; y < current.Height; y++)
            {
                if (comparer(current, previous, y))
                    equalRows++;
            }
            nanos[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        }
        var perFrame = equalRows / frames;
        if (expectedEqualRows < 0)
        {
            report.AppendLine($"| {name} | {Avg(nanos) / 1e6:F3} ms | {Pctl(nanos, 0.95) / 1e6:F3} ms | (基线,每帧相等行 {perFrame}) |");
            return perFrame;
        }
        report.AppendLine($"| {name} | {Avg(nanos) / 1e6:F3} ms | {Pctl(nanos, 0.95) / 1e6:F3} ms | {(perFrame == expectedEqualRows ? "是" : $"否(每帧 {perFrame} vs 基线 {expectedEqualRows})")} |");
        return perFrame;
    }

    private static bool RowEqualV0(CellGrid current, CellGrid previous, int y)
    {
        for (var x = 0; x < current.Width; x++)
        {
            if (current[x, y] != previous[x, y])
                return false;
        }
        return true;
    }

    private static bool RowEqualV1(CellGrid current, CellGrid previous, int y)
        => current.Row(y).SequenceEqual(previous.Row(y));

    private static bool RowEqualV2(CellGrid current, CellGrid previous, int y)
    {
        var a = MemoryMarshal.Cast<Cell, byte>(current.Row(y));
        var b = MemoryMarshal.Cast<Cell, byte>(previous.Row(y));
        var aWords = MemoryMarshal.Cast<byte, ulong>(a);
        var bWords = MemoryMarshal.Cast<byte, ulong>(b);
        var index = 0;
        while (index + 1 < aWords.Length)
        {
            if (aWords[index] != bWords[index] || aWords[index + 1] != bWords[index + 1])
                return false;
            index += 2;
        }
        if (index < aWords.Length && aWords[index] != bWords[index])
            return false;
        for (var tail = aWords.Length * sizeof(ulong); tail < a.Length; tail++)
        {
            if (a[tail] != b[tail])
                return false;
        }
        return true;
    }

    private static bool RowEqualV3(CellGrid current, CellGrid previous, int y)
        => MemoryMarshal.Cast<Cell, byte>(current.Row(y)).SequenceEqual(MemoryMarshal.Cast<Cell, byte>(previous.Row(y)));

    [Fact]
    public void Ansi_Renderer_Allocation_Benchmark()
    {
        var (width, height, label, frames) = Tiers[2];
        var lines = MarkdownCorpus.Build(CorpusLines, width, Seed);
        var grid = new CellGrid(width, height);
        var pooled = new char[width * (height + 8) * 8];

        FillStreaming(lines, grid, 0);
        var renderer = new AnsiRenderer();
        renderer.Render(grid, 0, 0);
        Collect();
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long outputChars = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            FillStreaming(lines, grid, frame);
            outputChars += renderer.Render(grid, 0, 0).Length;
        }
        var stringAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        renderer.Reset();
        renderer.Render(grid, 0, 0);
        Collect();
        allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < frames; frame++)
        {
            FillStreaming(lines, grid, frame);
            renderer.Render(grid, 0, 0, pooled.AsSpan());
        }
        var spanAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        var report = new StringBuilder();
        report.AppendLine("# AnsiRenderer 分配归因(可优化点-5)");
        report.AppendLine();
        report.AppendLine($"网格: {width}x{height}({label}),场景 A 全量流式,{frames} 帧。");
        report.AppendLine();
        report.AppendLine("| 路径 | GC 分配 | 每帧分配 |");
        report.AppendLine("| --- | --- | --- |");
        report.AppendLine($"| string Render(ToString 输出) | {stringAlloc / 1e6:F1} MB | {stringAlloc / (double)frames / 1024:F1} KB |");
        report.AppendLine($"| span Render(复用缓冲输出) | {spanAlloc / 1e6:F1} MB | {spanAlloc / (double)frames / 1024:F1} KB |");
        report.AppendLine();
        report.AppendLine($"每帧 ANSI 输出约 {outputChars / (double)frames / 1024:F1} KB 字符;string 路径分配 ≈ 输出字符数 x 2 字节(ToString 产物)。");
        WriteReport("render-ansi-alloc-benchmark.md", report);
    }

    private sealed record Metrics(long[] CpuNanos, long[] GpuNanos, long CpuAllocBytes, long GpuAllocBytes, long CpuOutputBytes, long GpuInstanceBytes);

    private sealed record Phase(long[] Nanos, long AllocBytes, long OutputBytes);

    private static Metrics Measure(AnsiRenderer renderer, CellGrid grid, CellGrid previous, Cell[][] lines, uint[] packed, List<(int Start, int Count)> ranges, int frames, bool streaming)
    {
        renderer.Reset();
        var cpu = MeasureCpu(renderer, grid, lines, frames, streaming);
        Collect();
        var gpu = MeasureGpu(grid, previous, lines, packed, ranges, frames, streaming);
        return new Metrics(cpu.Nanos, gpu.Nanos, cpu.AllocBytes, gpu.AllocBytes, cpu.OutputBytes, gpu.OutputBytes);
    }

    private static Phase MeasureCpu(AnsiRenderer renderer, CellGrid grid, Cell[][] lines, int frames, bool streaming)
    {
        var nanos = new long[frames];
        long outputBytes = 0;
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < frames; frame++)
        {
            Fill(lines, grid, frame, streaming);
            var started = Stopwatch.GetTimestamp();
            var output = renderer.Render(grid, 0, 0);
            nanos[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
            outputBytes += output.Length;
        }
        return new Phase(nanos, GC.GetAllocatedBytesForCurrentThread() - allocBefore, outputBytes);
    }

    private static Phase MeasureGpu(CellGrid grid, CellGrid previous, Cell[][] lines, uint[] packed, List<(int Start, int Count)> ranges, int frames, bool streaming)
    {
        var atlas = GlyphAtlas.Shared;
        for (var frame = 0; frame < 40; frame++)
        {
            Fill(lines, grid, frame, streaming);
            CellPacker.CollectDirtyRowRanges(grid, null, ranges);
            foreach (var (start, count) in ranges)
                CellPacker.PackRows(grid, start, count, packed, atlas);
        }
        Collect();
        var nanos = new long[frames];
        long outputBytes = 0;
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < frames; frame++)
        {
            Fill(lines, grid, frame, streaming);
            var started = Stopwatch.GetTimestamp();
            var dirtyRows = CellPacker.CollectDirtyRowRanges(grid, previous, ranges);
            foreach (var (start, count) in ranges)
                CellPacker.PackRows(grid, start, count, packed, atlas);
            nanos[frame] = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
            outputBytes += (long)dirtyRows * grid.Width * sizeof(uint);
            grid.CopyTo(previous);
        }
        return new Phase(nanos, GC.GetAllocatedBytesForCurrentThread() - allocBefore, outputBytes);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    internal static Cell[][] BuildPixelLines(int lineCount, int width, int seed)
    {
        var random = new Random(seed);
        var palette = Enum.GetValues<AnsiColor>();
        var lines = new Cell[lineCount][];
        for (var index = 0; index < lines.Length; index++)
        {
            var row = new Cell[width];
            for (var x = 0; x < width; x++)
                row[x] = new Cell('▀', palette[random.Next(palette.Length)], palette[random.Next(palette.Length)]);
            lines[index] = row;
        }
        return lines;
    }

    internal static Cell[][] BuildWideLines(int lineCount, int width, int seed)
    {
        var random = new Random(seed);
        var pool = new char[1500];
        for (var index = 0; index < pool.Length; index++)
            pool[index] = (char)('一' + random.Next(0x5000));
        AnsiColor[] foregrounds = [AnsiColor.Default, AnsiColor.White, AnsiColor.BrightWhite, AnsiColor.Cyan, AnsiColor.BrightCyan];
        var lines = new Cell[lineCount][];
        for (var index = 0; index < lines.Length; index++)
        {
            var foreground = foregrounds[random.Next(foregrounds.Length)];
            var row = new Cell[width];
            for (var x = 0; x + 1 < width; x += 2)
            {
                row[x] = new Cell(pool[random.Next(pool.Length)], foreground);
                row[x + 1] = new Cell('\0', foreground);
            }
            lines[index] = row;
        }
        return lines;
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

    private static void AppendTable(StringBuilder report, Metrics metrics, int frames)
    {
        report.AppendLine("| 指标 | CPU (AnsiRenderer) | GPU (顶点构建) |");
        report.AppendLine("| --- | --- | --- |");
        report.AppendLine($"| 平均帧耗时 | {Avg(metrics.CpuNanos) / 1e6:F3} ms | {Avg(metrics.GpuNanos) / 1e6:F3} ms |");
        report.AppendLine($"| P95 帧耗时 | {Pctl(metrics.CpuNanos, 0.95) / 1e6:F3} ms | {Pctl(metrics.GpuNanos, 0.95) / 1e6:F3} ms |");
        report.AppendLine($"| 最大帧耗时 | {metrics.CpuNanos.Max() / 1e6:F3} ms | {metrics.GpuNanos.Max() / 1e6:F3} ms |");
        report.AppendLine($"| 总耗时 | {metrics.CpuNanos.Sum() / 1e6:F1} ms | {metrics.GpuNanos.Sum() / 1e6:F1} ms |");
        report.AppendLine($"| GC 分配 | {metrics.CpuAllocBytes / 1e6:F1} MB | {metrics.GpuAllocBytes / 1e6:F1} MB |");
        report.AppendLine($"| 每帧产物大小 | {metrics.CpuOutputBytes / 1024.0 / frames:F1} KB ANSI 文本 | {metrics.GpuInstanceBytes / 1024.0 / frames:F1} KB 打包单元数据 |");
    }

    private static double Avg(long[] values) => values.Average();

    private static double Pctl(long[] values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * p) - 1];
    }

    private static void WriteReport(string fileName, StringBuilder report) => BenchReport.Write(fileName, report);
}
