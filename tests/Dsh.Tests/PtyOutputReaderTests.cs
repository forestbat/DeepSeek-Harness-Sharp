using System.Text;
using System.Threading.Channels;
using Dsh.Pty;

namespace Dsh.Tests;

/** Chunk-to-buffer reads shared by local PTY sessions, daemon subscriptions and their test doubles. */
public sealed class PtyOutputReaderTests
{
    private const int SmallBuffer = 4;

    /** The tail of a chunk larger than the buffer must arrive without any further output from the program. */
    [Fact(Timeout = 5000)]
    public async Task Tail_Of_A_Large_Chunk_Is_Read_Without_Waiting_For_More_Output()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var channel = Channel.CreateUnbounded<byte[]>();
        var reader = new PtyOutputReader(channel.Reader);
        channel.Writer.TryWrite("0123456789"u8.ToArray());

        var text = new StringBuilder();
        var buffer = new byte[SmallBuffer];
        while (text.Length < 10)
            text.Append(Encoding.ASCII.GetString(buffer, 0, await reader.ReadAsync(buffer, cancellationToken)));

        Assert.Equal("0123456789", text.ToString());
    }

    /** A completed channel still yields its buffered tail; 0 is returned only after the last byte. */
    [Fact(Timeout = 5000)]
    public async Task Completed_Channel_Drains_The_Tail_Before_Reporting_End()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var channel = Channel.CreateUnbounded<byte[]>();
        var reader = new PtyOutputReader(channel.Reader);
        channel.Writer.TryWrite("abcdef"u8.ToArray());
        channel.Writer.TryComplete();

        var buffer = new byte[SmallBuffer];
        Assert.Equal(SmallBuffer, await reader.ReadAsync(buffer, cancellationToken));
        Assert.Equal(2, await reader.ReadAsync(buffer, cancellationToken));
        Assert.Equal("ef", Encoding.ASCII.GetString(buffer, 0, 2));
        Assert.Equal(0, await reader.ReadAsync(buffer, cancellationToken));
    }

    [Fact(Timeout = 5000)]
    public async Task Empty_Buffer_Returns_Without_Waiting_For_Output()
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        var reader = new PtyOutputReader(channel.Reader);

        Assert.Equal(0, await reader.ReadAsync(Memory<byte>.Empty, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 5000)]
    public async Task Drain_Discards_The_Unread_Tail_And_Queued_Output()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var channel = Channel.CreateUnbounded<byte[]>();
        var reader = new PtyOutputReader(channel.Reader);
        channel.Writer.TryWrite("abcdef"u8.ToArray());
        channel.Writer.TryWrite("queued"u8.ToArray());
        var buffer = new byte[SmallBuffer];
        Assert.Equal(SmallBuffer, await reader.ReadAsync(buffer, cancellationToken));

        reader.DrainPendingOutput();
        channel.Writer.TryWrite("new"u8.ToArray());
        channel.Writer.TryComplete();

        Assert.Equal(3, await reader.ReadAsync(buffer, cancellationToken));
        Assert.Equal("new", Encoding.ASCII.GetString(buffer, 0, 3));
        Assert.Equal(0, await reader.ReadAsync(buffer, cancellationToken));
    }
}
