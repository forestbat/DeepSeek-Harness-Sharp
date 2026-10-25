using System.Text;
using Dsh.Gui.Terminal;
using Dsh.Pty;

namespace Dsh.Tests;

public sealed class TerminalSessionTests
{
    [Fact(Timeout = 5000)]
    public async Task Resizes_And_Input_Are_Sent_In_Order_While_The_First_Resize_Is_Pending()
    {
        var connection = new OrderedConnection();
        await using var session = new TerminalSession(connection, new TerminalSize(80, 24));
        session.Start();
        session.Resize(new TerminalSize(100, 30));
        await connection.FirstResize.Task.WaitAsync(TestContext.Current.CancellationToken);
        session.Resize(new TerminalSize(120, 40));
        session.Send("switch-window"u8);
        Assert.Equal(["resize:100x30"], connection.Calls);
        connection.ReleaseResize.TrySetResult();
        await connection.InputWritten.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["resize:100x30", "resize:120x40", "input:switch-window"], connection.Calls);
        lock (session.Gate)
        {
            var frame = new TerminalFrame();
            frame.Capture(session.Screen, 0);
            Assert.Equal((120, 40), (frame.Columns, frame.Rows));
        }
    }

    private sealed class OrderedConnection : ITerminalConnection
    {
        private readonly Lock _gate = new();
        private readonly List<string> _calls = [];
        public TaskCompletionSource FirstResize { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseResize { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InputWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string[] Calls
        {
            get
            {
                lock (_gate)
                    return [.. _calls];
            }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            lock (_gate)
                _calls.Add($"input:{Encoding.UTF8.GetString(bytes.Span)}");
            InputWritten.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken)
        {
            bool first;
            lock (_gate)
            {
                first = _calls.Count == 0;
                _calls.Add($"resize:{size.Columns}x{size.Rows}");
            }
            if (first)
            {
                FirstResize.TrySetResult();
                await ReleaseResize.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
