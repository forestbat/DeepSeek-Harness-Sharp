using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Dsh.Tui;
using Dsh.Tui.Vk;
using static Dsh.Tests.VkRenderBenchmarkTests;

namespace Dsh.Tests;

/**
 * Vk 后端渲染基准(离屏): 走 VkRenderCore + VkOffscreenTarget —— 与 VkOffscreenRenderer / VkOffscreenHost 同一套录制逻辑。
 * 档位/语料/帧数与 RenderBenchmarkTests 对齐(同 CorpusLines=4000 / Seed=20260916 / 每档 1000 帧 / markdown 语料)。
 * 分段: prepare(整屏打包 + 图集脏槽/字形映射/params 上传 + 影子刷槽)、record(两趟 CmdDraw 录制)、submit(提交)、wait(等 fence)、readback(回读)。
 * 口径: 未改产品代码, 仅在测试侧用 Stopwatch 包住 VkRenderCore/VkOffscreenTarget 的公开入口; 渲染语义与产品一致(恒全帧重绘)。
 * 运行: Dsh.Tests.exe -noColor -class Dsh.Tests.VkRenderBenchmarkTests -reporter verbose (Windows/Linux 均可, 无显示也能跑)。
 * 注意: 同进程并存两个 VkGpuDevice 退出时会 fail-fast(OpenTK VKLoader.SetInstance 进程级单例), 窗口档在独立的 VkWindowRenderBenchmarkTests, 需分进程运行。
 */
[Collection("RenderBench")]
[Trait("Category", "RenderBench")]
[Trait("VkBench", "Offscreen")]
public class VkRenderBenchmarkTests
{
    private const int CorpusLines = 4000;
    private const int Seed = 20260916;
    private const int WarmupFrames = 40;
    private const int ReadbackFrames = 50;
    private const int GlyphMapSide = 256;
    private const int ParamsBytes = (4 * 16) + (64 * 16) + (2 * 16);
    private const int CellsMinCapacity = 80 * 25;

    private static readonly (int Width, int Height, string Label, int Frames)[] Tiers =
    [
        (240, 67, "240x67 格", 1000),
        (320, 90, "320x90 格", 1000),
        (480, 135, "480x135 格", 1000),
        (640, 180, "640x180 格", 1000),
    ];

    private static readonly string[] PhaseNames = ["prepare", "record", "submit", "wait", "readback"];

    [Fact]
    public void Vk_Offscreen_Render_Benchmark()
    {
        GlyphAtlas atlas;
        try
        {
            atlas = GlyphAtlas.Shared;
            atlas.Prewarm();
        }
        catch (Exception error)
        {
            Assert.Skip($"无法构建字形图集: {error.Message}");
            return;
        }

        VkGpuDevice gpu;
        try
        {
            gpu = VkGpuDevice.Create();
        }
        catch (Exception error)
        {
            Assert.Skip($"无可用 Vulkan 设备: {error.Message}");
            return;
        }

        var lines = MarkdownCorpus.Build(CorpusLines, Tiers[^1].Width, Seed);
        // 先把整个语料的字形烘进图集: 测量段只反映稳态构建, 不含一次性光栅化(与既有基准同口径)。
        foreach (var line in lines)
            atlas.EnsureBaked(line);

        var report = new StringBuilder();
        report.AppendLine("# Vk 后端渲染基准(markdown 语料, 离屏路径)");
        report.AppendLine();
        report.AppendLine($"设备: vk: {gpu.DeviceName}; 环境: {OsTag()}/{RuntimeInformation.RuntimeIdentifier}。");
        report.AppendLine($"语料: {CorpusLines} 行合成 markdown(Seed={Seed}, 同 RenderBenchmarkTests), 每档 {Tiers[0].Frames} 帧全量流式; 预热 {WarmupFrames} 帧后测量。");
        report.AppendLine("路径: VkRenderCore(Prepare/RecordFrame) + VkOffscreenTarget, 与 VkOffscreenRenderer/VkOffscreenHost 同一套录制逻辑; 恒全帧重绘。");
        report.AppendLine("分段: prepare=PackCells(整屏打包, 含缺失字形烘焙)+图集脏槽/字形映射/params 上传+整屏影子刷槽; record=BeginFrame+两趟 CmdDraw; submit=提交(image→buffer 拷贝/layout/QueueSubmit); wait=等 fence; readback=ReadPixels(行翻转拷回); total=每帧全程。");
        report.AppendLine("口径: 未改产品代码, Stopwatch 只包住 VkRenderCore/VkOffscreenTarget 的公开入口; 渲染语义与产品一致。");
        report.AppendLine();

        using (gpu)
        {
            var core = new VkRenderCore(gpu, atlas);
            try
            {
                foreach (var (width, height, label, frames) in Tiers)
                    RunTier(report, gpu, core, atlas, lines, width, height, label, frames);
            }
            finally
            {
                core.Dispose();
            }
        }

        var fileName = $"vk-render-bench-{OsTag()}-{RuntimeInformation.RuntimeIdentifier}.txt";
        BenchReport.Write(fileName, report);
        Console.Out.WriteLine($"[vk-bench] 离屏报告: tests/.scratch/bench/{fileName}");
    }

    private static void RunTier(StringBuilder report, VkGpuDevice gpu, VkRenderCore core, GlyphAtlas atlas,
        Cell[][] lines, int width, int height, string label, int frames)
    {
        var pixelWidth = width * core.GlyphWidth;
        var pixelHeight = height * core.GlyphHeight;
        var target = VkOffscreenTarget.Create(gpu, core, pixelWidth, pixelHeight);
        core.Target = target;
        try
        {
            var grid = new CellGrid(width, height);
            var packed = new uint[width * height];
            var pixels = new byte[pixelWidth * pixelHeight * 4];
            Warmup(core, target, lines, grid, width, height);
            target.WaitSlotIdle();
            var measurement = Measure(core, target, atlas, lines, grid, width, height, frames, pixels, packed);
            var resident = EstimateResident(atlas, width, height, core.GlyphWidth, core.GlyphHeight);
            AppendTier(report, label, width, height, frames, pixelWidth, pixelHeight, measurement, resident);
        }
        finally
        {
            target.Dispose();
        }
    }

    private static void Warmup(VkRenderCore core, VkOffscreenTarget target, Cell[][] lines, CellGrid grid, int width, int height)
    {
        for (var frame = 0; frame < WarmupFrames; frame++)
        {
            FillStreaming(lines, grid, frame);
            target.WaitSlotIdle();
            core.Prepare(grid);
            if (!target.BeginFrame())
                continue;
            core.RecordFrame(target.CommandBuffer, target.CurrentFramebuffer, target.CurrentExtent.Width, target.CurrentExtent.Height, width, height);
            target.MarkRendered();
            target.Present();
        }
    }

    private static TierMeasurement Measure(VkRenderCore core, VkOffscreenTarget target, GlyphAtlas atlas, Cell[][] lines,
        CellGrid grid, int width, int height, int frames, byte[] pixels, uint[] packed)
    {
        var sampler = new FrameRecorder(new long[frames], new long[frames], new long[frames], new long[frames], new long[frames]);
        Collect();
        var managedBefore = GC.GetAllocatedBytesForCurrentThread();
        var gcBefore = GC.GetTotalMemory(false);
        var wsBefore = WorkingSet();
        for (var frame = 0; frame < frames; frame++)
            RecordFrameSample(sampler, frame, lines, grid, width, height, target, core);
        var managedBytes = GC.GetAllocatedBytesForCurrentThread() - managedBefore;
        var gcAfter = GC.GetTotalMemory(false);
        var wsAfter = WorkingSet();
        var readback = MeasureReadback(core, target, lines, grid, width, height, pixels);
        var pack = MeasurePack(lines, grid, packed, atlas, frames);
        return new TierMeasurement(sampler, readback, pack, managedBytes, gcBefore, gcAfter, wsBefore, wsAfter);
    }

    private static void RecordFrameSample(FrameRecorder sampler, int frame, Cell[][] lines, CellGrid grid, int width, int height,
        VkOffscreenTarget target, VkRenderCore core)
    {
        FillStreaming(lines, grid, frame);
        var start = Stopwatch.GetTimestamp();
        target.WaitSlotIdle();
        var afterWait = Stopwatch.GetTimestamp();
        core.Prepare(grid);
        var afterPrepare = Stopwatch.GetTimestamp();
        if (target.BeginFrame())
        {
            core.RecordFrame(target.CommandBuffer, target.CurrentFramebuffer, target.CurrentExtent.Width, target.CurrentExtent.Height, width, height);
            target.MarkRendered();
        }
        var afterRecord = Stopwatch.GetTimestamp();
        target.Present();
        var afterSubmit = Stopwatch.GetTimestamp();
        target.WaitSlotIdle();
        var afterWait2 = Stopwatch.GetTimestamp();
        sampler.Prepare[frame] = Ns(afterWait, afterPrepare);
        sampler.Record[frame] = Ns(afterPrepare, afterRecord);
        sampler.Submit[frame] = Ns(afterRecord, afterSubmit);
        sampler.Wait[frame] = Ns(start, afterWait) + Ns(afterSubmit, afterWait2);
        sampler.Total[frame] = Ns(start, afterWait2);
    }

    /** 回读单独采样(每帧回读成本与帧数无关, 无需 1000 帧): 渲染一帧后只计 TryReadPixels(行翻转 + 读映射缓冲)。 */
    private static long[] MeasureReadback(VkRenderCore core, VkOffscreenTarget target, Cell[][] lines, CellGrid grid,
        int width, int height, byte[] pixels)
    {
        var nanos = new long[ReadbackFrames];
        for (var frame = 0; frame < ReadbackFrames; frame++)
        {
            FillStreaming(lines, grid, frame);
            target.WaitSlotIdle();
            core.Prepare(grid);
            if (target.BeginFrame())
            {
                core.RecordFrame(target.CommandBuffer, target.CurrentFramebuffer, target.CurrentExtent.Width, target.CurrentExtent.Height, width, height);
                target.MarkRendered();
            }
            target.Present();
            target.WaitSlotIdle();
            var started = Stopwatch.GetTimestamp();
            target.TryReadPixels(pixels, target.Width, target.Height);
            nanos[frame] = Ns(started, Stopwatch.GetTimestamp());
        }
        return nanos;
    }

    private static long[] MeasurePack(Cell[][] lines, CellGrid grid, uint[] packed, GlyphAtlas atlas, int frames)
    {
        var nanos = new long[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            FillStreaming(lines, grid, frame);
            var started = Stopwatch.GetTimestamp();
            CellPacker.PackRows(grid, 0, grid.Height, packed, atlas);
            nanos[frame] = Ns(started, Stopwatch.GetTimestamp());
        }
        return nanos;
    }

    private static void AppendTier(StringBuilder report, string label, int width, int height, int frames,
        int pixelWidth, int pixelHeight, TierMeasurement m, Resident resident)
    {
        var renderNs = m.Samples.Total;
        var phases = new[] { m.Samples.Prepare, m.Samples.Record, m.Samples.Submit, m.Samples.Wait, m.Readback };
        report.AppendLine($"## {label}: {width}x{height} = {width * height} 格, {frames} 帧, 目标 {pixelWidth}x{pixelHeight} px");
        report.AppendLine();
        report.AppendLine("| 口径 | 均帧 | P50 | P95 | 最大 | fps |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- |");
        AppendFrameRow(report, "渲染(render=prepare+record+submit+wait)", renderNs);
        AppendFrameRow(report, $"回读(ReadPixels, 单独采样 {m.Readback.Length} 帧)", m.Readback);
        report.AppendLine();
        AppendPhaseTable(report, phases, m.Pack, renderNs);
        report.AppendLine();
        AppendMemory(report, frames, m, resident);
        report.AppendLine();
        AppendMachineLines(report, label, phases, m.Pack, renderNs);
        report.AppendLine();
        if (width == 480)
            AppendHorizontal(report, phases, m.Pack, renderNs);
        PrintOffscreenLine(label, renderNs, m, phases);
    }

    private static void AppendPhaseTable(StringBuilder report, long[][] phases, long[] pack, long[] renderNs)
    {
        var renderAvg = Avg(renderNs);
        report.AppendLine("| 分段 | 均帧 | P95 | 占渲染帧 |");
        report.AppendLine("| --- | --- | --- | --- |");
        for (var index = 0; index < phases.Length; index++)
            report.AppendLine($"| {PhaseNames[index]} | {Ms(Avg(phases[index])):F3} | {Ms(Pctl(phases[index], 0.95)):F3} | {Avg(phases[index]) / renderAvg * 100:F1}% |");
        report.AppendLine($"| pack(单独测: CellPacker.PackRows 整屏) | {Ms(Avg(pack)):F3} | {Ms(Pctl(pack, 0.95)):F3} | {Avg(pack) / renderAvg * 100:F1}% |");
    }

    private static void AppendMemory(StringBuilder report, int frames, TierMeasurement m, Resident resident)
    {
        report.AppendLine("| 内存 | 值 | 来源 |");
        report.AppendLine("| --- | --- | --- |");
        report.AppendLine($"| 每帧托管分配 | {m.ManagedBytes / (double)frames / 1024:F2} KB ({m.ManagedBytes} B / {frames} 帧) | GC.GetAllocatedBytesForCurrentThread 差值(预热后稳态) |");
        report.AppendLine($"| GC.GetTotalMemory(false) | {m.GcBefore / 1024.0 / 1024:F1} → {m.GcAfter / 1024.0 / 1024:F1} MiB (Δ {(m.GcAfter - m.GcBefore) / 1024.0:F1} KiB) | 测量段前后 |");
        report.AppendLine($"| WorkingSet64 | {m.WsBefore / 1024.0 / 1024:F1} → {m.WsAfter / 1024.0 / 1024:F1} MiB (Δ {(m.WsAfter - m.WsBefore) / 1024.0:F1} KiB) | Process.GetCurrentProcess().WorkingSet64 |");
        report.AppendLine($"| 常驻显存/缓冲合计(估算) | {resident.TotalDevice / 1024.0 / 1024:F2} MiB | 见下(从对象/常量算出) |");
        report.AppendLine($"| - atlas image R8 | {resident.Atlas / 1024.0:F1} KiB | AtlasWidth*AtlasHeight |");
        report.AppendLine($"| - glyph map R32_SINT | {resident.GlyphMap / 1024.0:F1} KiB | GlyphMapSide^2*4 |");
        report.AppendLine($"| - cells texel buffers x{VkRenderCore.FrameSlots} | {resident.Cells / 1024.0 / 1024:F2} MiB | FrameSlots*max(W*H,{CellsMinCapacity})*4 |");
        report.AppendLine($"| - params UBO x{VkRenderCore.FrameSlots} | {resident.Params / 1024.0:F1} KiB | FrameSlots*{ParamsBytes} |");
        report.AppendLine($"| - staging x{VkRenderCore.FrameSlots} | {resident.Staging / 1024.0 / 1024:F2} MiB | FrameSlots*max(atlas, GlyphMapSide^2*4) |");
        report.AppendLine($"| - 离屏目标 image | {resident.Target / 1024.0 / 1024:F2} MiB | 目标像素*4 |");
        report.AppendLine($"| - 回读缓冲(host-visible) | {resident.Readback / 1024.0 / 1024:F2} MiB | 目标像素*4 |");
        report.AppendLine($"| - CPU 影子 _packed(托管) | {resident.Shadow / 1024.0:F1} KiB | max(W*H,{CellsMinCapacity})*4 |");
    }

    private static void AppendMachineLines(StringBuilder report, string label, long[][] phases, long[] pack, long[] renderNs)
    {
        var renderAvg = Avg(renderNs);
        report.AppendLine("```text");
        for (var index = 0; index < phases.Length; index++)
            report.AppendLine($"tier={label} phase={PhaseNames[index]} ms={Avg(phases[index]) / 1e6:F4} p95={Pctl(phases[index], 0.95) / 1e6:F4} pct={Avg(phases[index]) / renderAvg * 100:F1}");
        report.AppendLine($"tier={label} phase=pack ms={Avg(pack) / 1e6:F4} p95={Pctl(pack, 0.95) / 1e6:F4} pct={Avg(pack) / renderAvg * 100:F1}");
        report.AppendLine("```");
    }

    private static void AppendHorizontal(StringBuilder report, long[][] phases, long[] pack, long[] renderNs)
    {
        var renderAvg = Avg(renderNs);
        report.AppendLine("### 单帧各阶段横向对照(480x135)");
        report.AppendLine();
        report.Append($"prepare {Ms(Avg(phases[0])):F3} ms ({Avg(phases[0]) / renderAvg * 100:F1}%)");
        report.Append($" | record {Ms(Avg(phases[1])):F3} ms ({Avg(phases[1]) / renderAvg * 100:F1}%)");
        report.Append($" | submit {Ms(Avg(phases[2])):F3} ms ({Avg(phases[2]) / renderAvg * 100:F1}%)");
        report.Append($" | wait {Ms(Avg(phases[3])):F3} ms ({Avg(phases[3]) / renderAvg * 100:F1}%)");
        report.Append($" | readback {Ms(Avg(phases[4])):F3} ms ({Avg(phases[4]) / renderAvg * 100:F1}%)");
        report.AppendLine($" | pack(单独) {Ms(Avg(pack)):F3} ms ({Avg(pack) / renderAvg * 100:F1}%)");
        report.AppendLine();
    }

    private static void PrintOffscreenLine(string label, long[] renderNs, TierMeasurement m, long[][] phases)
    {
        var hottest = HottestIndex(phases);
        Console.Out.WriteLine($"[vk-bench] {label}: render {Ms(Avg(renderNs)):F3} ms (P50 {Ms(Pctl(renderNs, 0.50)):F3}, P95 {Ms(Pctl(renderNs, 0.95)):F3}, max {Ms(renderNs.Max()):F3}), {Fps(renderNs):F1} fps; 回读 {Ms(Avg(m.Readback)):F3} ms(采样 {m.Readback.Length}); 托管/帧 {m.ManagedBytes / (double)renderNs.Length:F0} B; 热点段 {PhaseNames[hottest]} ({Avg(phases[hottest]) / Avg(renderNs) * 100:F1}%)");
    }

    private static int HottestIndex(long[][] phases)
    {
        var best = 0;
        for (var index = 1; index < phases.Length; index++)
        {
            if (Avg(phases[index]) > Avg(phases[best]))
                best = index;
        }
        return best;
    }

    private static Resident EstimateResident(GlyphAtlas atlas, int width, int height, int glyphWidth, int glyphHeight)
    {
        var capacity = Math.Max(width * height, CellsMinCapacity);
        var atlasBytes = (long)atlas.AtlasWidth * atlas.AtlasHeight;
        var glyphMapBytes = (long)GlyphMapSide * GlyphMapSide * 4;
        var targetBytes = (long)(width * glyphWidth) * (height * glyphHeight) * 4;
        return new Resident(
            atlasBytes,
            glyphMapBytes,
            (long)VkRenderCore.FrameSlots * capacity * 4,
            (long)VkRenderCore.FrameSlots * ParamsBytes,
            VkRenderCore.FrameSlots * Math.Max(atlasBytes, (long)GlyphMapSide * GlyphMapSide * 4),
            targetBytes,
            targetBytes,
            (long)capacity * 4);
    }

    internal static long[] Combine(int frames, params long[][] arrays)
    {
        var result = new long[frames];
        for (var index = 0; index < frames; index++)
        {
            long sum = 0;
            foreach (var array in arrays)
                sum += array[index];
            result[index] = sum;
        }
        return result;
    }

    internal static void AppendFrameRow(StringBuilder report, string name, long[] nanos)
        => report.AppendLine($"| {name} | {Ms(Avg(nanos)):F3} | {Ms(Pctl(nanos, 0.50)):F3} | {Ms(Pctl(nanos, 0.95)):F3} | {Ms(nanos.Max()):F3} | {Fps(nanos):F1} |");

    internal static long Ns(long start, long end) => (long)Stopwatch.GetElapsedTime(start, end).TotalNanoseconds;

    internal static double Ms(double nanos) => nanos / 1e6;

    internal static double Avg(long[] values) => values.Average();

    internal static long Pctl(long[] values, double percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
    }

    internal static double Fps(long[] nanos) => 1_000_000_000.0 / nanos.Average();

    internal static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    internal static long WorkingSet() => Process.GetCurrentProcess().WorkingSet64;

    internal static string OsTag() => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    internal static void FillStreaming(Cell[][] lines, CellGrid grid, int frame)
    {
        for (var row = 0; row < grid.Height; row++)
            grid.SetRow(row, lines[(frame + row) % lines.Length]);
    }

    private sealed record FrameRecorder(long[] Prepare, long[] Record, long[] Submit, long[] Wait, long[] Total);

    private sealed record TierMeasurement(FrameRecorder Samples, long[] Readback, long[] Pack, long ManagedBytes, long GcBefore, long GcAfter, long WsBefore, long WsAfter);

    private sealed record Resident(long Atlas, long GlyphMap, long Cells, long Params, long Staging, long Target, long Readback, long Shadow)
    {
        public long TotalDevice => Atlas + GlyphMap + Cells + Params + Staging + Target;
    }
}

/**
 * Vk 后端渲染基准(窗口/交换链): 走 VkWindowRenderer(与 VkSurfaceHost 同一套交换链路径), 用于对比"离屏 vs 上屏"。
 * 无显示服务器时自动 skip(不 fail); 与离屏档并存两个 VkGpuDevice 会 fail-fast, 必须分进程运行。
 * 运行: Dsh.Tests.exe -noColor -class Dsh.Tests.VkWindowRenderBenchmarkTests -reporter verbose
 */
[Collection("RenderBench")]
[Trait("Category", "RenderBench")]
[Trait("VkBench", "Window")]
public unsafe class VkWindowRenderBenchmarkTests
{
    private const int CorpusLines = 4000;
    private const int Seed = 20260916;
    private const int WarmupFrames = 40;
    private const int Frames = 1000;

    private static readonly (int Width, int Height, string Label)[] Tiers =
    [
        (240, 67, "240x67 格"),
        (320, 90, "320x90 格"),
        (480, 135, "480x135 格"),
        (640, 180, "640x180 格"),
    ];

    [Fact]
    public void Vk_Window_Render_Benchmark()
    {
        if (!OperatingSystem.IsWindows() && !GpuRenderer.TryDetectDisplay(out var displayReason))
        {
            Assert.Skip($"无显示服务器, 跳过 Vk 窗口档: {displayReason}");
            return;
        }

        GlyphAtlas atlas;
        try
        {
            atlas = GlyphAtlas.Shared;
            atlas.Prewarm();
        }
        catch (Exception error)
        {
            Assert.Skip($"无法构建字形图集: {error.Message}");
            return;
        }

        VkSurfaceHost host;
        try
        {
            host = VkSurfaceHost.Create("dsh --gpu (vk bench)", 640 * atlas.GlyphWidth, 180 * atlas.GlyphHeight, hidden: true, vsync: false);
        }
        catch (Exception error)
        {
            Assert.Skip($"无法创建 Vk 窗口/表面: {error.Message}");
            return;
        }

        // SDL 窗口默认不可缩放: 少了 SDL_WINDOW_RESIZABLE 时标题栏最大化/双击/拖边都不可用(GL 侧 GLFW 默认可缩放, 故只有 Vk 形态会这样)。
        Assert.True(
            (SDL.SDL3.SDL_GetWindowFlags((SDL.SDL_Window*)host.Window) & SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE) != 0,
            "Vk 窗口必须带 SDL_WINDOW_RESIZABLE");

        var lines = MarkdownCorpus.Build(CorpusLines, Tiers[^1].Width, Seed);
        foreach (var line in lines)
            atlas.EnsureBaked(line);

        host.CreateRenderCore(atlas);
        var renderer = host.Renderer!;
        var core = renderer.Core;

        var report = new StringBuilder();
        report.AppendLine("# Vk 后端渲染基准(markdown 语料, 窗口/交换链)");
        report.AppendLine();
        report.AppendLine($"设备: vk: {host.DeviceName}; 环境: {OsTag()}/{RuntimeInformation.RuntimeIdentifier}; 交换链 present mode: {renderer.PresentMode}。");
        report.AppendLine($"语料: {CorpusLines} 行合成 markdown(Seed={Seed}, 同 RenderBenchmarkTests), 每档 {Frames} 帧全量流式; 预热 {WarmupFrames} 帧后测量。");
        report.AppendLine("路径: VkWindowRenderer(acquire→Prepare→RecordFrame→present), 与 VkSurfaceHost 生产路径一致; 窗口 HIDDEN(不抢焦点), vsync 关。");
        report.AppendLine("分段: wait=WaitSlotIdle(只等当前帧槽); prepare=core.Prepare(整屏打包+上传); record=BeginFrame+两趟 CmdDraw; submit=Present(QueueSubmit+QueuePresent); render=prepare+record+submit。");
        report.AppendLine();

        using (host)
        {
            foreach (var (width, height, label) in Tiers)
                RunWindowTier(report, host, renderer, core, lines, width, height, label);

            // 窗口路径字形几何探针: 单格 'A' 的屏幕墨迹竖向跨度必须等于图集槽位跨度
            // (不等就是采样/上传把字形压扁, 用户看到的是"字符下半被黑边盖住")。
            host.Resize(2 * atlas.GlyphWidth, atlas.GlyphHeight);
            var probe = new CellGrid(2, 1);
            probe.SetRow(0, [new Cell('A'), new Cell(' ')]);
            renderer.DrawFrame(probe);
            renderer.RequestCapture();
            renderer.DrawFrame(probe);
            var shot = new byte[renderer.CurrentExtent.Width * renderer.CurrentExtent.Height * 4];
            if (renderer.TryGetCapture(shot))
            {
                var renderedSpan = InkSpan(shot, (int)renderer.CurrentExtent.Width, atlas, 0);
                var atlasSpan = AtlasInkSpan(atlas, 'A');
                Assert.True(
                    Math.Abs((renderedSpan.Last - renderedSpan.First) - (atlasSpan.Last - atlasSpan.First)) <= 1,
                    $"窗口路径字形被压扁: 屏幕墨迹 y={renderedSpan.First}..{renderedSpan.Last} ≠ 图集 y={atlasSpan.First}..{atlasSpan.Last}");
            }

            // 宽字(CJK 占两格)在窗口路径必须与图集一致(用户报告的"字体下方被遮住/剪切"就在这里暴露)。
            var wideProbe = new CellGrid(2, 1);
            wideProbe.SetRow(0, [new Cell('计'), new Cell('\0')]);
            renderer.DrawFrame(wideProbe);
            renderer.RequestCapture();
            renderer.DrawFrame(wideProbe);
            if (renderer.TryGetCapture(shot))
            {
                var renderedSpan = InkSpan(shot, (int)renderer.CurrentExtent.Width, atlas, 0, 2 * atlas.GlyphWidth);
                var atlasSpan = AtlasInkSpan(atlas, '计', 2 * atlas.GlyphWidth);
                Assert.True(
                    Math.Abs((renderedSpan.Last - renderedSpan.First) - (atlasSpan.Last - atlasSpan.First)) <= 1,
                    $"窗口路径宽字被压扁: 屏幕墨迹 y={renderedSpan.First}..{renderedSpan.Last} ≠ 图集 y={atlasSpan.First}..{atlasSpan.Last}");
            }
        }

        var fileName = $"vk-render-bench-window-{OsTag()}-{RuntimeInformation.RuntimeIdentifier}.txt";
        BenchReport.Write(fileName, report);
        Console.Out.WriteLine($"[vk-bench] 窗口报告: tests/.scratch/bench/{fileName}");
    }

    /** 一段墨迹的竖向范围(含首末行); 由调用方自行换算跨度。 */
    private readonly record struct InkBand(int First, int Last);

    /** 屏幕缓冲里某格内墨迹(任一通道 >128)的竖向范围; 捕获与图集行序可能相反, 只比跨度。 */
    private static InkBand InkSpan(byte[] pixels, int width, GlyphAtlas atlas, int cellX, int spanWidth = 0)
    {
        if (spanWidth <= 0)
            spanWidth = atlas.GlyphWidth;
        var first = int.MaxValue;
        var last = -1;
        for (var y = 0; y < atlas.GlyphHeight; y++)
        {
            for (var x = 0; x < spanWidth; x++)
            {
                var index = (((y * width) + (cellX * atlas.GlyphWidth)) + x) * 4;
                if (pixels[index] <= 128 && pixels[index + 1] <= 128 && pixels[index + 2] <= 128)
                    continue;
                first = Math.Min(first, y);
                last = Math.Max(last, y);
            }
        }

        return new InkBand(first, last);
    }

    /** 图集槽位内某字形的墨迹竖向范围(与 Bake 写 _textureData 的行序一致)。 */
    private static InkBand AtlasInkSpan(GlyphAtlas atlas, char character, int spanWidth = 0)
    {
        if (spanWidth <= 0)
            spanWidth = atlas.GlyphWidth;
        var slot = (atlas.MapUpload[character] & 0x7FFFFFFF) - 1;
        if (slot < 0)
            return new InkBand(-1, -1);
        var slotX = (slot % GlyphAtlas.Columns) * atlas.GlyphWidth;
        var slotY = (slot / GlyphAtlas.Columns) * atlas.GlyphHeight;
        var data = atlas.TextureData;
        var first = int.MaxValue;
        var last = -1;
        for (var y = 0; y < atlas.GlyphHeight; y++)
        {
            for (var x = 0; x < spanWidth; x++)
            {
                if (data[((slotY + y) * atlas.AtlasWidth) + slotX + x] <= 128)
                    continue;
                first = Math.Min(first, y);
                last = Math.Max(last, y);
            }
        }

        return new InkBand(first, last);
    }

    private static void RunWindowTier(StringBuilder report, VkSurfaceHost host, VkWindowRenderer renderer, VkRenderCore core,
        Cell[][] lines, int width, int height, string label)
    {
        host.Resize(width * core.GlyphWidth, height * core.GlyphHeight);
        var grid = new CellGrid(width, height);
        for (var frame = 0; frame < WarmupFrames; frame++)
            DrawWindowFrame(renderer, core, lines, grid, width, height, frame);
        renderer.WaitSlotIdle();

        var sampler = new WindowSampler(new long[Frames], new long[Frames], new long[Frames], new long[Frames], new long[Frames]);
        Collect();
        var managedBefore = GC.GetAllocatedBytesForCurrentThread();
        var gcBefore = GC.GetTotalMemory(false);
        var wsBefore = WorkingSet();
        for (var frame = 0; frame < Frames; frame++)
            RecordWindowSample(sampler, renderer, core, lines, grid, width, height, frame);
        var measurement = new WindowMeasurement(sampler, GC.GetAllocatedBytesForCurrentThread() - managedBefore, gcBefore, GC.GetTotalMemory(false), wsBefore, WorkingSet());
        AppendWindowTier(report, label, width, height, (int)renderer.Extent.width, (int)renderer.Extent.height, measurement);
    }

    private static void DrawWindowFrame(VkWindowRenderer renderer, VkRenderCore core, Cell[][] lines, CellGrid grid, int width, int height, int frame)
    {
        FillStreaming(lines, grid, frame);
        renderer.WaitSlotIdle();
        core.Prepare(grid);
        if (!renderer.BeginFrame())
            return;
        core.RecordFrame(renderer.CommandBuffer, renderer.CurrentFramebuffer, renderer.CurrentExtent.Width, renderer.CurrentExtent.Height, width, height);
        renderer.MarkRendered();
        renderer.Present();
    }

    private static void RecordWindowSample(WindowSampler sampler, VkWindowRenderer renderer, VkRenderCore core, Cell[][] lines,
        CellGrid grid, int width, int height, int frame)
    {
        FillStreaming(lines, grid, frame);
        var start = Stopwatch.GetTimestamp();
        renderer.WaitSlotIdle();
        var afterWait = Stopwatch.GetTimestamp();
        core.Prepare(grid);
        var afterPrepare = Stopwatch.GetTimestamp();
        if (renderer.BeginFrame())
        {
            core.RecordFrame(renderer.CommandBuffer, renderer.CurrentFramebuffer, renderer.CurrentExtent.Width, renderer.CurrentExtent.Height, width, height);
            renderer.MarkRendered();
        }
        var afterRecord = Stopwatch.GetTimestamp();
        renderer.Present();
        var afterSubmit = Stopwatch.GetTimestamp();
        sampler.Prepare[frame] = Ns(afterWait, afterPrepare);
        sampler.Record[frame] = Ns(afterPrepare, afterRecord);
        sampler.Submit[frame] = Ns(afterRecord, afterSubmit);
        sampler.Wait[frame] = Ns(start, afterWait);
        sampler.Total[frame] = Ns(start, afterSubmit);
    }

    private static void AppendWindowTier(StringBuilder report, string label, int width, int height, int pixelWidth, int pixelHeight, WindowMeasurement m)
    {
        var renderNs = Combine(Frames, m.Samples.Prepare, m.Samples.Record, m.Samples.Submit);
        var phases = new[] { m.Samples.Prepare, m.Samples.Record, m.Samples.Submit, m.Samples.Wait };
        string[] names = ["prepare", "record", "submit", "wait"];
        report.AppendLine($"## {label}: {width}x{height} 格, {Frames} 帧, 交换链 {pixelWidth}x{pixelHeight} px");
        report.AppendLine();
        report.AppendLine("| 口径 | 均帧 | P50 | P95 | 最大 | fps |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- |");
        AppendFrameRow(report, "渲染(prepare+record+submit)", renderNs);
        AppendFrameRow(report, "全程(+wait)", m.Samples.Total);
        report.AppendLine();
        var renderAvg = Avg(renderNs);
        report.AppendLine("| 分段 | 均帧 | P95 | 占渲染帧 |");
        report.AppendLine("| --- | --- | --- | --- |");
        for (var index = 0; index < phases.Length; index++)
            report.AppendLine($"| {names[index]} | {Ms(Avg(phases[index])):F3} | {Ms(Pctl(phases[index], 0.95)):F3} | {Avg(phases[index]) / renderAvg * 100:F1}% |");
        report.AppendLine();
        report.AppendLine("| 内存 | 值 | 来源 |");
        report.AppendLine("| --- | --- | --- |");
        report.AppendLine($"| 每帧托管分配 | {m.ManagedBytes / (double)Frames / 1024:F2} KB | GC.GetAllocatedBytesForCurrentThread 差值(稳态) |");
        report.AppendLine($"| GC.GetTotalMemory(false) | {m.GcBefore / 1024.0 / 1024:F1} → {m.GcAfter / 1024.0 / 1024:F1} MiB | 测量段前后 |");
        report.AppendLine($"| WorkingSet64 | {m.WsBefore / 1024.0 / 1024:F1} → {m.WsAfter / 1024.0 / 1024:F1} MiB | Process.WorkingSet64 |");
        report.AppendLine();
        report.AppendLine("```text");
        for (var index = 0; index < phases.Length; index++)
            report.AppendLine($"tier={label} phase={names[index]} ms={Avg(phases[index]) / 1e6:F4} p95={Pctl(phases[index], 0.95) / 1e6:F4} pct={Avg(phases[index]) / renderAvg * 100:F1}");
        report.AppendLine("```");
        Console.Out.WriteLine($"[vk-bench-window] {label}: render {Ms(Avg(renderNs)):F3} ms (P95 {Ms(Pctl(renderNs, 0.95)):F3}), {Fps(renderNs):F1} fps; 托管/帧 {m.ManagedBytes / (double)Frames:F0} B; extent {pixelWidth}x{pixelHeight}");
    }

    private sealed record WindowSampler(long[] Prepare, long[] Record, long[] Submit, long[] Wait, long[] Total);

    private sealed record WindowMeasurement(WindowSampler Samples, long ManagedBytes, long GcBefore, long GcAfter, long WsBefore, long WsAfter);
}
