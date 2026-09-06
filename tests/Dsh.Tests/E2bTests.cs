using System.Net;
using System.Text;
using Dsh.E2b;

namespace Dsh.Tests;

public sealed class E2bTests
{
    [Fact]
    public async Task CreateSandbox_And_ExecuteCommand()
    {
        var handler = new FakeHandler([
            new FakeResponse("/sandboxes", """{"sandboxId":"sb_test"}"""),
            new FakeResponse("/sandboxes/sb_test/commands", """{"stdout":"hello","stderr":"","exitCode":0}"""),
        ]);
        using var http = new HttpClient(handler);
        var client = new E2bClient(http, "https://e2b.test");

        var sandboxId = await client.CreateSandboxAsync("code-runner");
        Assert.Equal("sb_test", sandboxId);

        var result = await client.ExecuteCommandAsync(sandboxId, "echo hello");
        Assert.Equal("hello", result.Stdout);
        Assert.Equal(0, result.ExitCode);
    }

    private sealed record FakeResponse(string Path, string Body);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<FakeResponse> _responses;

        public FakeHandler(IEnumerable<FakeResponse> responses)
        {
            _responses = new Queue<FakeResponse>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
