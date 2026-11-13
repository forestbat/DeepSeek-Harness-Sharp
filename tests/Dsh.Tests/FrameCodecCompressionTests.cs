using Dsh.Transport;

namespace Dsh.Tests;

/**
 * 帧层压缩: 会话事件是高度重复的 JSON, 原样传输的数据量是落盘体积的几十倍。
 * 这里既要保证压缩/未压缩两种帧都能互读, 也要量化到底省了多少字节。
 */
public sealed class FrameCodecCompressionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /** 未压缩的小帧必须逐字节原样往返: 旧对端读新帧走的正是这条路。 */
    [Fact]
    public async Task SmallFrame_RoundTripsUncompressed()
    {
        var payload = "{\"method\":\"ping\"}"u8.ToArray();
        var wire = await EncodeAsync(payload);
        Assert.Equal(payload, await DecodeAsync(wire));
        // 小载荷不压缩: 线上就是 4 字节头 + 原文。
        Assert.Equal(4 + payload.Length, wire.Length);
    }

    /** 重复度高的载荷要被压小, 且解压后与原文完全一致。 */
    [Fact]
    public async Task RepetitiveFrame_ShrinksAndRoundTrips()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("{\"type\":\"assistant_chunk\",\"text\":\"xxxx\"},", 400)));
        var wire = await EncodeAsync(payload);
        var restored = await DecodeAsync(wire);
        Assert.Equal(payload, restored);
        Assert.True(wire.Length < payload.Length / 4,
            $"压缩后 {wire.Length} 字节, 原文 {payload.Length} 字节, 没有明显变小");
    }

    /** 不可压缩的内容不能被"压缩"到更大: 标志位只在真的变小时才置。 */
    [Fact]
    public async Task IncompressibleFrame_IsNotInflated()
    {
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);
        var wire = await EncodeAsync(payload);
        var restored = await DecodeAsync(wire);
        Assert.Equal(payload.Length, restored.Length);
        Assert.Equal(payload, restored);
        Assert.True(wire.Length <= payload.Length + 4, $"随机内容被撑到 {wire.Length} 字节(原文 {payload.Length})");
    }

    /** 连续多帧要能各自独立解码: 压缩标志不能串到下一帧。 */
    [Fact]
    public async Task MixedFrames_KeepTheirOwnPayloads()
    {
        var small = "{}"u8.ToArray();
        var big = System.Text.Encoding.UTF8.GetBytes(new string('a', 4000));
        using var stream = new MemoryStream();
        await FrameCodec.WriteFrameAsync(stream, small, Ct);
        await FrameCodec.WriteFrameAsync(stream, big, Ct);
        await FrameCodec.WriteFrameAsync(stream, small, Ct);
        stream.Position = 0;
        Assert.Equal(small, await FrameCodec.ReadFrameAsync(stream, cancellationToken: Ct));
        Assert.Equal(big, await FrameCodec.ReadFrameAsync(stream, cancellationToken: Ct));
        Assert.Equal(small, await FrameCodec.ReadFrameAsync(stream, cancellationToken: Ct));
    }

    /** 量化收益: 真实会话事件的压缩比, 决定这条优化值不值。 */
    [Fact]
    public async Task TypicalSessionEvents_CompressSubstantially()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(SampleEvents());
        var wire = await EncodeAsync(payload);
        var ratio = (double)payload.Length / wire.Length;
        TestContext.Current.SendDiagnosticMessage(
            $"sample {payload.Length / 1024}KB -> {wire.Length / 1024}KB ({ratio:F1}x)");
        Assert.True(ratio > 5, $"压缩比只有 {ratio:F1}x, 收益不足");
    }

    /** 构造接近真实会话的事件载荷: 大量重复字段名与相近文本。 */
    private static string SampleEvents()
    {
        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < 200; index += 1)
            builder.Append("{\"seq\":").Append(index)
                .Append(",\"type\":\"assistant_chunk\",\"turn\":1,\"step\":1,\"payload\":{\"kind\":\"text\",\"text\":\"")
                .Append("正在分析文件并整理结果，请稍候。".PadRight(120, '。'))
                .Append("\"}},");
        return builder.ToString();
    }

    private static async Task<byte[]> EncodeAsync(byte[] payload)
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteFrameAsync(stream, payload, Ct);
        return stream.ToArray();
    }

    private static async Task<byte[]> DecodeAsync(byte[] wire)
    {
        using var stream = new MemoryStream(wire);
        var frame = await FrameCodec.ReadFrameAsync(stream, cancellationToken: Ct);
        return frame ?? throw new InvalidOperationException("no frame");
    }
}
