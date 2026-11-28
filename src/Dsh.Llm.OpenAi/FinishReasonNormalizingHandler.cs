using System.Text;

namespace Dsh.Llm.OpenAi;

public sealed class FinishReasonNormalizingHandler : DelegatingHandler
{
    public FinishReasonNormalizingHandler(HttpMessageHandler innerHandler)
    {
        InnerHandler = innerHandler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Content is not null)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            response.Content = new StreamContent(new LineNormalizingStream(stream));
        }
        return response;
    }

    private sealed class LineNormalizingStream : Stream
    {
        private readonly StreamReader _reader;
        private readonly MemoryStream _pending = new();
        private int _pendingPosition;

        public LineNormalizingStream(Stream inner)
        {
            _reader = new StreamReader(inner, Encoding.UTF8);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            if (count == 0)
                return 0;
            if (_pendingPosition >= _pending.Length)
                FillPending();
            return CopyFromPending(buffer.AsSpan(offset, count));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty)
                return 0;
            if (_pendingPosition >= _pending.Length)
                await FillPendingAsync(cancellationToken).ConfigureAwait(false);
            return CopyFromPending(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        private void FillPending()
        {
            ResetPending();
            WritePendingLine(_reader.ReadLine());
        }

        private async ValueTask FillPendingAsync(CancellationToken cancellationToken)
        {
            ResetPending();
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            WritePendingLine(line);
        }

        private void ResetPending()
        {
            _pending.SetLength(0);
            _pending.Position = 0;
            _pendingPosition = 0;
        }

        private void WritePendingLine(string? line)
        {
            if (line is null)
                return;
            var bytes = Encoding.UTF8.GetBytes($"{NormalizeLine(line)}\n");
            _pending.Write(bytes, 0, bytes.Length);
        }

        private int CopyFromPending(Span<byte> destination)
        {
            if (_pendingPosition >= _pending.Length)
                return 0;
            var available = Math.Min(destination.Length, (int)_pending.Length - _pendingPosition);
            _pending.GetBuffer().AsSpan(_pendingPosition, available).CopyTo(destination);
            _pendingPosition += available;
            return available;
        }

        private static string NormalizeLine(string line) => line
            .Replace("\"finish_reason\":\"\"", "\"finish_reason\":null", StringComparison.Ordinal)
            .Replace("\"finish_reason\": \"\"", "\"finish_reason\": null", StringComparison.Ordinal)
            .Replace("\"type\":\"\"", "\"type\":\"function\"", StringComparison.Ordinal)
            .Replace("\"type\": \"\"", "\"type\": \"function\"", StringComparison.Ordinal)
            // Responses 流上 logprobs 在 output_text.delta/.done 是必填数组
            // （TypeSpec 无 |null）。个别中转/上游会透传 null，openai-dotnet 2.x 的
            // 流事件反序列化对它直接 EnumerateArray()，把整条流炸掉
            // （"requires an element of type 'Array', but the target element has
            // type 'Null'"）。归一为空数组，对两种事件与 content part 均合法。
            .Replace("\"logprobs\":null", "\"logprobs\":[]", StringComparison.Ordinal)
            .Replace("\"logprobs\": null", "\"logprobs\": []", StringComparison.Ordinal);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader.Dispose();
                _pending.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _pending.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}