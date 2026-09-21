using System.Net;
using System.Text;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.E2b;
using Dsh.Llm;
using Dsh.Runtime;
using E2bPlugin = Dsh.E2b.Plugin;

namespace Dsh.Tests;

public sealed class E2bTests
{
    [Fact]
    public async Task CreateSandbox_And_ExecuteCommand()
    {
        var handler = new FakeHandler([
            new FakeResponse("""{"sandboxId":"sb_test"}"""),
            new FakeResponse("""{"stdout":"hello","stderr":"","exitCode":0}"""),
        ]);
        using var http = new HttpClient(handler);
        var client = new E2bClient(http, "https://e2b.test");

        var sandboxId = await client.CreateSandboxAsync("code-runner", TestContext.Current.CancellationToken);
        Assert.Equal("sb_test", sandboxId);

        var result = await client.ExecuteCommandAsync(sandboxId, "echo hello", TestContext.Current.CancellationToken);
        Assert.Equal("hello", result.Stdout);
        Assert.Equal(0, result.ExitCode);
    }

    public sealed class ToolExecution
    {
        [Fact]
        public async Task Run_ExecutesCommandAndKillsSandbox()
        {
            using var host = new E2bHarness();

            var result = await host.Execute("e2b_run", """{"command":"echo hi"}""");

            Assert.IsType<ToolExecutionResult.Success>(result);
            Assert.Contains("exitCode=0", host.TextOf(result));
            Assert.Contains(host.Requests, request => request.Method == "DELETE" && request.Path.EndsWith("/sandboxes/sb_test"));
        }

        [Fact]
        public async Task Run_WithoutCommand_Fails()
        {
            using var host = new E2bHarness();

            var result = await host.Execute("e2b_run", """{}""");

            Assert.IsType<ToolExecutionResult.Failure>(result);
        }
    }

    public sealed class PluginSelfCheck
    {
        [Fact]
        public async Task Compose_RegistersE2bToolWhenConfigured()
        {
            var root = Path.Combine(Path.GetTempPath(), $"dsh-e2b-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "settings.yaml"), """
                    plugins:
                      "@deepseek-ai/dsh-e2b":
                        apiKey: sk-test
                        baseUrl: https://e2b.test
                    """);
                var home = HarnessHome.Resolve(root);
                using var app = await ConfigBoot.Compose(new HarnessOptions(home, root));

                var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
                Assert.NotNull(tools.Get("e2b_run"));
                Assert.Equal(ActivationState.Active, app.Composition!.Find("@deepseek-ai/dsh-tool-e2b")?.State);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (IOException)
                {
                }
            }
        }

        [Fact]
        public void Runtime_IsSkippedWithoutApiKey()
        {
            var ctx = new Context();
            using var registration = new E2bPlugin("@deepseek-ai/dsh-e2b").Apply(ctx, new Dictionary<string, object?>
            {
                ["apiKeyEnv"] = "DSH_TEST_MISSING_E2B_KEY",
            });

            Assert.Null(ctx.Get<E2bRuntime>(E2bRuntime.ServiceName, false));
            Assert.Contains(ctx.Logger.Buffer, message => message.Text.Contains("no API key"));
        }

        [Fact]
        public void Runtime_IsProvidedWithInlineKey()
        {
            var ctx = new Context();
            using var registration = new E2bPlugin("@deepseek-ai/dsh-e2b").Apply(ctx, new Dictionary<string, object?>
            {
                ["apiKey"] = "sk-test",
                ["baseUrl"] = "https://e2b.test",
            });

            Assert.NotNull(ctx.Get<E2bRuntime>(E2bRuntime.ServiceName, false));
        }
    }

    private sealed class E2bHarness : IDisposable
    {
        private readonly Context _ctx = new();
        private readonly ToolRuntime _tools;
        private readonly HttpClient _http;
        private readonly IDisposable _tool;
        private int _counter;

        public E2bHarness()
        {
            _ = new SystemPrompt(_ctx, new SystemPromptConfig());
            _tools = new ToolRuntime(_ctx);
            var handler = new FakeHandler([
                new FakeResponse("""{"sandboxId":"sb_test"}"""),
                new FakeResponse("""{"stdout":"hi","stderr":"","exitCode":0}"""),
                new FakeResponse("{}"),
            ]);
            _http = new HttpClient(handler);
            _ = new E2bRuntime(_ctx, new E2bRuntimeConfig { ApiKey = "sk-test", BaseUrl = "https://e2b.test" }, _http);
            _tool = ToolE2bTool.Register(_ctx, E2bRuntimeConfig.DefaultTimeoutMs);
            Requests = handler.Requests;
        }

        public List<(string Method, string Path)> Requests { get; }

        public Task<ToolExecutionResult> Execute(string name, string arguments)
            => _tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{++_counter}"),
                Name = name,
                Arguments = JsonDocument.Parse(arguments).RootElement,
                Signal = default,
            });

        public string TextOf(ToolExecutionResult result)
            => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

        public void Dispose()
        {
            _tool.Dispose();
            _http.Dispose();
        }
    }

    private sealed record FakeResponse(string Body);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<FakeResponse> _responses;

        public FakeHandler(IEnumerable<FakeResponse> responses)
        {
            _responses = new Queue<FakeResponse>(responses);
        }

        public List<(string Method, string Path)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri?.AbsolutePath ?? ""));
            var response = _responses.Count > 0 ? _responses.Dequeue() : new FakeResponse("{}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
