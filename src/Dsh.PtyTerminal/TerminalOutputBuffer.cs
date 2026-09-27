using System.Buffers;
using System.Text;

namespace Dsh.PtyTerminal;

internal sealed class TerminalOutputBuffer
{
    /**
     * 有界文本环形缓冲: 以 char[] 承载, 从头部淘汰来维持行数上限与 UTF-8 字节预算。
     * 容量取 maxBytes 个 char 足够: 合法 UTF-16 文本的 UTF-8 字节数恒不小于其 char 数, 故预算内的内容必能容纳。
     */
    private readonly int _maxBytes;
    private readonly int? _maxLines;
    private readonly char[] _buffer;
    private readonly object _gate = new();
    private int _head;
    private int _count;
    private int _newlineCount;
    private int _utf8Bytes;
    private bool _dropped;

    public TerminalOutputBuffer(int maxBytes, int? maxLines = null)
    {
        _maxBytes = maxBytes;
        _maxLines = maxLines;
        _buffer = new char[Math.Max(1, maxBytes)];
    }

    public bool Truncated
    {
        get
        {
            lock (_gate)
                return _dropped;
        }
    }

    public void Append(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return;
        lock (_gate)
            AppendUnlocked(text);
    }

    public TerminalSendRead Consume()
    {
        lock (_gate)
        {
            var delta = MaterializeUnlocked();
            var truncated = _dropped;
            _head = 0;
            _count = 0;
            _newlineCount = 0;
            _utf8Bytes = 0;
            _dropped = false;
            return new TerminalSendRead(delta, truncated);
        }
    }

    public (string Text, bool Truncated) Snapshot()
    {
        lock (_gate)
            return (MaterializeUnlocked(), _dropped);
    }

    /**
     * 按"距尾部行偏移 + 行数"在环上定位区间, 只把最终返回的字符复制到池化缓冲, 物化在锁外完成。
     */
    public TerminalReadResult Read(int offset, int count, int maxBytes)
    {
        int totalLines;
        int length;
        bool truncated;
        char[]? rented = null;
        lock (_gate)
        {
            totalLines = _count == 0 ? 0 : _newlineCount + 1;
            if (offset >= totalLines)
                return new TerminalReadResult("", totalLines, offset, offset, _dropped);
            var end = totalLines - offset;
            var start = Math.Max(0, end - count);
            var begin = start == 0 ? 0 : FindNewlineFromTail(totalLines - start) + 1;
            var endExclusive = end == totalLines ? _count : FindNewlineFromTail(totalLines - end);
            var window = ByteWindow(begin, endExclusive, maxBytes);
            length = endExclusive - window.Begin;
            if (length > 0)
            {
                rented = ArrayPool<char>.Shared.Rent(length);
                CopyRange(window.Begin, rented.AsSpan(0, length));
            }
            truncated = _dropped || window.Truncated;
        }
        try
        {
            var text = length == 0 ? "" : new string(rented!, 0, length);
            var returnedLines = text.Length == 0 ? 0 : CountLines(text);
            return new TerminalReadResult(text, totalLines, offset, offset + returnedLines, truncated);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    private void AppendUnlocked(ReadOnlySpan<char> text)
    {
        var capacity = _buffer.Length;
        var overflow = _count + text.Length - capacity;
        if (overflow > 0)
        {
            if (overflow < _count)
            {
                DropFrontToFit(overflow);
            }
            else
            {
                var skip = text.Length - capacity;
                _head = 0;
                _count = 0;
                _newlineCount = 0;
                _utf8Bytes = 0;
                _dropped = true;
                if (skip > 0)
                    text = text[skip..];
            }
        }
        var physical = _head + _count;
        if (physical >= capacity)
            physical -= capacity;
        var first = Math.Min(text.Length, capacity - physical);
        text[..first].CopyTo(_buffer.AsSpan(physical, first));
        if (first < text.Length)
            text[first..].CopyTo(_buffer);
        _count += text.Length;
        _newlineCount += CountNewlines(text);
        _utf8Bytes += Encoding.UTF8.GetByteCount(text);
        EnforceLineLimit();
        EnforceByteLimit();
    }

    private void DropFrontToFit(int minimum)
    {
        var removed = 0;
        while (removed < minimum && _count > 0)
            removed += DropHeadRune();
    }

    private void EnforceLineLimit()
    {
        if (_maxLines is not { } maxLines)
            return;
        while (_newlineCount + 1 > maxLines && _count > 0)
        {
            while (_count > 0)
            {
                var isNewline = _buffer[_head] == '\n';
                DropHeadRune();
                if (isNewline)
                    break;
            }
        }
    }

    private void EnforceByteLimit()
    {
        while (_utf8Bytes > _maxBytes && _count > 0)
            DropHeadRune();
    }

    private int DropHeadRune()
    {
        var capacity = _buffer.Length;
        var c = _buffer[_head];
        int chars;
        int bytes;
        if (char.IsHighSurrogate(c) && _count > 1 && char.IsLowSurrogate(CharAt(1)))
        {
            chars = 2;
            bytes = 4;
        }
        else
        {
            chars = 1;
            bytes = Utf8BytesOfChar(c);
        }
        if (c == '\n')
            _newlineCount--;
        _head += chars;
        if (_head >= capacity)
            _head -= capacity;
        _count -= chars;
        _utf8Bytes -= bytes;
        _dropped = true;
        return chars;
    }

    private string MaterializeUnlocked()
        => _count == 0 ? "" : string.Create(_count, this, static (span, self) => self.CopyRange(0, span));

    private void CopyRange(int start, Span<char> destination)
    {
        if (destination.Length == 0)
            return;
        var capacity = _buffer.Length;
        var physical = _head + start;
        if (physical >= capacity)
            physical -= capacity;
        var first = Math.Min(destination.Length, capacity - physical);
        _buffer.AsSpan(physical, first).CopyTo(destination);
        if (first < destination.Length)
            _buffer.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
    }

    /** 返回从尾部数第 nth 个换行的逻辑下标(nth 从 1 起)。 */
    private int FindNewlineFromTail(int nth)
    {
        var remaining = nth;
        for (var logical = _count - 1; logical >= 0; logical--)
        {
            if (CharAt(logical) == '\n' && --remaining == 0)
                return logical;
        }
        return -1;
    }

    /** 从区间尾部按 UTF-8 字节预算回退, 返回可容纳的最长后缀起点与是否发生截断。 */
    private (int Begin, bool Truncated) ByteWindow(int begin, int endExclusive, int maxBytes)
    {
        var bytes = 0;
        var index = endExclusive;
        while (index > begin)
        {
            var c = CharAt(index - 1);
            int runeBytes;
            int chars;
            if (char.IsLowSurrogate(c) && index - 1 > begin && char.IsHighSurrogate(CharAt(index - 2)))
            {
                runeBytes = 4;
                chars = 2;
            }
            else
            {
                runeBytes = Utf8BytesOfChar(c);
                chars = 1;
            }
            if (bytes + runeBytes > maxBytes)
                break;
            bytes += runeBytes;
            index -= chars;
        }
        return (index, index > begin);
    }

    private char CharAt(int logicalIndex)
    {
        var physical = _head + logicalIndex;
        var capacity = _buffer.Length;
        if (physical >= capacity)
            physical -= capacity;
        return _buffer[physical];
    }

    private static int CountNewlines(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (c == '\n')
                count++;
        }
        return count;
    }

    private static int CountLines(string text)
    {
        var lines = 1;
        foreach (var c in text)
        {
            if (c == '\n')
                lines++;
        }
        return lines;
    }

    private static int Utf8BytesOfChar(char c)
    {
        if (char.IsSurrogate(c))
            return 3;
        if (c < 0x80)
            return 1;
        return c < 0x800 ? 2 : 3;
    }
}

internal sealed class BufferedSendOperation : TerminalSendOperation
{
    private readonly TerminalOutputBuffer _output;
    private readonly TaskCompletionSource<TerminalSendResult> _promise = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<BufferedSendOperation> _onCancel;
    private bool _finished;
    private bool _cancellationRequested;

    public BufferedSendOperation(int maxBytes, Action<BufferedSendOperation> onCancel)
    {
        _output = new TerminalOutputBuffer(maxBytes);
        _onCancel = onCancel;
    }

    public Task<TerminalSendResult> Done => _promise.Task;

    public bool Settled => _finished;

    public bool CancelRequested => _cancellationRequested;

    public void Append(string text)
    {
        if (!_finished)
            _output.Append(text);
    }

    public void Settle(TerminalWaitReason waitReason, TerminalSessionStatus sessionStatus, bool inheritedTruncation)
    {
        if (_finished)
            return;
        _finished = true;
        var read = _output.Snapshot();
        _promise.TrySetResult(new TerminalSendResult(read.Text, waitReason, sessionStatus, read.Truncated || inheritedTruncation));
    }

    public void Fail(Exception error)
    {
        if (_finished)
            return;
        _finished = true;
        _promise.TrySetException(error);
    }

    public TerminalSendRead ReadOutput() => _output.Consume();

    public bool Cancel()
    {
        if (_finished)
            return false;
        _cancellationRequested = true;
        _onCancel(this);
        return true;
    }
}
