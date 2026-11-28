using System.Net;
using System.Text;
using Dsh.Fleet;
using Dsh.Llm;
using Dsh.Llm.Anthropic;
using Dsh.Llm.OpenAi;

namespace Dsh.Tests;

public sealed class ReviewRound1PlatformLlmTests
{
    private const string AnthropicModel = "claude-opus-5";

    [Fact]
    public async Task FinishReasonNormalizingHandler_StalledAsyncRead_CancelsAndDisposesInnerStream()
    {
        var innerStream = new StalledFirstLineThenBlockStream("data: {\"finish_reason\":\"\"}\n");
        using var innerHandler = new StreamResponseHandler(innerStream);
        using var client = new HttpClient(new FinishReasonNormalizingHandler(innerHandler));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat/completions");
        using var cts = new CancellationTokenSource();

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using (var responseStream = await response.Content.ReadAsStreamAsync(cts.Token))
        using (var reader = new StreamReader(responseStream, Encoding.UTF8))
        {
            var firstLine = await reader.ReadLineAsync(cts.Token);
            Assert.Equal("data: {\"finish_reason\":null}", firstLine);

            var pendingSecondLine = reader.ReadLineAsync(cts.Token).AsTask();
            await innerStream.WaitForSecondReadAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pendingSecondLine.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }

        Assert.True(innerStream.Disposed);
    }

    [Fact]
    public async Task FinishReasonNormalizingHandler_DisposeAsync_DisposesInnerStream()
    {
        var innerStream = new StalledFirstLineThenBlockStream("data: {\"type\":\"\",\"logprobs\":null}\n");
        using var innerHandler = new StreamResponseHandler(innerStream);
        using var client = new HttpClient(new FinishReasonNormalizingHandler(innerHandler));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/responses");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        var responseStream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var buffer = new byte[128];
        var read = await responseStream.ReadAsync(buffer, 0, buffer.Length, TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        Assert.Equal("data: {\"type\":\"function\",\"logprobs\":[]}\n", text);

        await responseStream.DisposeAsync();
        Assert.True(innerStream.Disposed);
    }

    [Fact]
    public void WriteWindowsAskPass_PreservesUtf8WithoutBomAndBatchMetacharacters_AndDeleteRemovesSidecar()
    {
        const string password = "p@ss&word|1<2>3^4%PATH%!VAR!\"'()中文🔐";
        var commandPath = SshTransport.WriteWindowsAskPass(password);
        var sidecarPath = $"{commandPath}.txt";
        try
        {
            Assert.EndsWith(".cmd", commandPath, StringComparison.Ordinal);
            Assert.True(File.Exists(commandPath));
            Assert.True(File.Exists(sidecarPath));

            Assert.Equal("@echo off\r\ntype \"%~f0.txt\"\r\n", File.ReadAllText(commandPath, Encoding.UTF8));

            var rawBytes = File.ReadAllBytes(sidecarPath);
            Assert.False(rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF);
            Assert.Equal($"{password}\r\n", Encoding.UTF8.GetString(rawBytes));

            SshTransport.DeleteAskPass(commandPath);
            Assert.False(File.Exists(commandPath));
            Assert.False(File.Exists(sidecarPath));
        }
        finally
        {
            SshTransport.TryDelete(commandPath);
        }
    }

    [Fact]
    public void WriteAskPass_And_TryDelete_ManageCurrentPlatformCredentialFiles()
    {
        const string password = "secret&value|中文";
        var path = SshTransport.WriteAskPass(password);
        try
        {
            Assert.True(File.Exists(path));
            if (OperatingSystem.IsWindows())
                Assert.True(File.Exists($"{path}.txt"));

            SshTransport.TryDelete(path);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists($"{path}.txt"));
        }
        finally
        {
            SshTransport.TryDelete(path);
        }
    }

    [Fact]
    public async Task AnthropicSse_MergesMessageStartAndMessageDeltaUsage_AndEmitsOnMessageStopWhenDeltaHasNoUsage()
    {
        string[] sseWithCache =
        [
            """event: message_start""",
            """data: {"type":"message_start","message":{"id":"msg_1","role":"assistant","content":[],"usage":{"input_tokens":10,"cache_read_input_tokens":40,"cache_creation_input_tokens":5,"output_tokens":0}}}""",
            "",
            """data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            "",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}""",
            "",
            """data: {"type":"content_block_stop","index":0}""",
            "",
            """data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":8}}""",
            "",
            """data: {"type":"message_stop"}""",
            "",
        ];
        var cacheAssembler = await StreamAnthropicAsync(sseWithCache);
        Assert.NotNull(cacheAssembler.Usage);
        Assert.Equal(55, cacheAssembler.Usage.InputTokens);
        Assert.Equal(8, cacheAssembler.Usage.OutputTokens);
        Assert.Equal(63, cacheAssembler.Usage.TotalTokens);
        Assert.Equal(40, cacheAssembler.Usage.CacheReadTokens);
        Assert.Equal(5, cacheAssembler.Usage.CacheWriteTokens);

        string[] sseStopOnly =
        [
            """event: message_start""",
            """data: {"type":"message_start","message":{"id":"msg_2","role":"assistant","content":[],"usage":{"input_tokens":15,"output_tokens":0}}}""",
            "",
            """data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            "",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}""",
            "",
            """data: {"type":"content_block_stop","index":0}""",
            "",
            """data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}""",
            "",
            """data: {"type":"message_stop"}""",
            "",
        ];
        var stopAssembler = await StreamAnthropicAsync(sseStopOnly);
        Assert.NotNull(stopAssembler.Usage);
        Assert.Equal(15, stopAssembler.Usage.InputTokens);
        Assert.Equal(0, stopAssembler.Usage.OutputTokens);
        Assert.Equal(15, stopAssembler.Usage.TotalTokens);
    }

    private static async Task<BlockAssembler> StreamAnthropicAsync(IReadOnlyList<string> sseLines)
    {
        using var handler = new AnthropicScriptedHandler(sseLines);
        using var httpClient = new HttpClient(handler);
        var adapter = new AnthropicAdapter("anthropic", "https://example.invalid", "sk-test", [AnthropicModel], httpClient);
        var options = new GenerateOptions
        {
            Provider = "anthropic",
            Model = AnthropicModel,
            Messages = [MessageFactory.CreateUserText("hi")],
        };
        var assembler = new BlockAssembler();
        await foreach (var chunk in adapter.Stream(options, TestContext.Current.CancellationToken))
            assembler.Push(chunk);
        return assembler;
    }

    private sealed class StreamResponseHandler(Stream responseStream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(responseStream),
            });
    }

    private sealed class StalledFirstLineThenBlockStream(string firstChunk) : Stream
    {
        private readonly byte[] _first = Encoding.UTF8.GetBytes(firstChunk);
        private readonly TaskCompletionSource _secondReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public bool Disposed { get; private set; }

        public Task WaitForSecondReadAsync() => _secondReadStarted.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                var toCopy = Math.Min(buffer.Length, _first.Length);
                _first.AsSpan(0, toCopy).CopyTo(buffer.Span);
                return toCopy;
            }
            _secondReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AnthropicScriptedHandler(IReadOnlyList<string> sseLines) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{string.Join('\n', sseLines)}\n", Encoding.UTF8, "text/event-stream"),
            });
    }
}
