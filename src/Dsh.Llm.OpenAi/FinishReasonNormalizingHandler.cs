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
            if (_pendingPosition >= _pending.Length)
                FillPending();
            if (_pendingPosition >= _pending.Length)
                return 0;
            var available = Math.Min(count, (int)_pending.Length - _pendingPosition);
            Array.Copy(_pending.GetBuffer(), _pendingPosition, buffer, offset, available);
            _pendingPosition += available;
            return available;
        }

        private void FillPending()
        {
            _pending.SetLength(0);
            _pending.Position = 0;
            _pendingPosition = 0;
            var line = _reader.ReadLine();
            if (line is null)
                return;
            var normalized = line
                .Replace("\"finish_reason\":\"\"", "\"finish_reason\":null", StringComparison.Ordinal)
                .Replace("\"finish_reason\": \"\"", "\"finish_reason\": null", StringComparison.Ordinal)
                .Replace("\"type\":\"\"", "\"type\":\"function\"", StringComparison.Ordinal)
                .Replace("\"type\": \"\"", "\"type\": \"function\"", StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(normalized + "\n");
            _pending.Write(bytes, 0, bytes.Length);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}