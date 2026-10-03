using System.Net;
using System.Text;
using System.Text.Json;
using Dsh.A2A;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Interaction.AskUser;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class A2aTests
{
    private static readonly string[] SseTextOnly =
    [
        """data: {"choices":[{"delta":{"role":"assistant","content":null}}]}""",
        "",
        """data: {"choices":[{"delta":{"content":"Hello"}}]}""",
        "",
        """data: {"choices":[{"delta":{"content":" world"}}]}""",
        "",
        """data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}""",
        "",
        "data: [DONE]",
        "",
    ];

    private sealed class A2aRpcError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private sealed class MockDeepSeekServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, IReadOnlyList<string>> _script;
        private readonly TimeSpan _responseDelay;

        public MockDeepSeekServer(Func<string, IReadOnlyList<string>> script, TimeSpan? responseDelay = null)
        {
            _script = script;
            _responseDelay = responseDelay ?? TimeSpan.Zero;
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(Serve);
        }

        public int Port { get; }

        public string BaseUrl => $"http://127.0.0.1:{Port}";

        public string? LastRequestBody { get; private set; }

        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    LastRequestBody = await reader.ReadToEndAsync();
                    if (_responseDelay > TimeSpan.Zero)
                        await Task.Delay(_responseDelay, _stop.Token);
                    context.Response.ContentType = "text/event-stream";
                    foreach (var line in _script(LastRequestBody))
                    {
                        var bytes = Encoding.UTF8.GetBytes($"{line}\n");
                        await context.Response.OutputStream.WriteAsync(bytes);
                        await context.Response.OutputStream.FlushAsync();
                    }
                    context.Response.Close();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (IOException)
                {
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private sealed class A2aFixture : IDisposable
    {
        public Context Ctx { get; }
        public A2aSessionBridge Bridge { get; }
        public A2aHttpListener Listener { get; }
        public HttpClient Client { get; }

        public A2aFixture(string llmBaseUrl, string? authToken = null)
        {
            Ctx = new Context();
            _ = new SessionStore(Ctx);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            _ = new ToolRuntime(Ctx);
            var llm = new LlmRuntime(Ctx);
            _ = new AgentRegistry(Ctx);
            _ = new AgentLoop(Ctx);
            var connection = new Dsh.Llm.DeepSeek.DeepSeekConnectionOptions(
                llmBaseUrl,
                "TEST_KEY",
                new Dsh.Llm.DeepSeek.RequestDefaults(),
                Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultMaxTokens,
                Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultContextWindowValue,
                [new Dsh.Llm.DeepSeek.DeepSeekCatalogModel("test-model")],
                Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
                ResolvedRetryPolicy.Resolve(null, "test"));
            var adapter = new Dsh.Llm.DeepSeek.DeepSeekAdapter("test-provider", new Dsh.Llm.DeepSeek.DeepSeekAdapterOptions
            {
                Options = () => connection,
                ResolveApiKey = (_, _) => Task.FromResult("sk-test"),
                ResolveUserId = () => "test-user",
            });
            llm.RegisterAdapter(["test-provider"], adapter);
            Bridge = new A2aSessionBridge(Ctx, "test-provider", "test-model");
            Listener = new A2aHttpListener(new A2aJsonRpc(Bridge), new A2aServerOptions(AuthToken: authToken), Ctx);
            Listener.Start();
            Client = new HttpClient { BaseAddress = new Uri(Listener.Endpoint) };
        }

        public async Task<JsonElement> RpcAsync(string method, object? parameters, string? bearer = null)
        {
            var response = await PostAsync(method, parameters, bearer);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new A2aRpcError(error.GetProperty("code").GetInt32(), error.GetProperty("message").GetString()!);
            return document.RootElement.GetProperty("result").Clone();
        }

        public async Task<HttpResponseMessage> PostAsync(string method, object? parameters, string? bearer = null, string? a2aVersion = null)
        {
            var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "req-1", method, @params = parameters });
            var request = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            if (bearer is not null)
                request.Headers.Add("Authorization", $"Bearer {bearer}");
            if (a2aVersion is not null)
                request.Headers.Add("A2A-Version", a2aVersion);
            return await Client.SendAsync(request);
        }

        public async Task<List<JsonElement>> ReadSseAsync(string method, object? parameters)
        {
            var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "req-1", method, @params = parameters });
            var request = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("text/event-stream");
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var frames = new List<JsonElement>();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            var data = new StringBuilder();
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line["data:".Length..].TrimStart());
                }
                else if (line.Length == 0 && data.Length > 0)
                {
                    frames.Add(JsonDocument.Parse(data.ToString()).RootElement.GetProperty("result").Clone());
                    data.Clear();
                }
            }
            return frames;
        }

        public async Task<JsonElement> WaitTaskStateAsync(string taskId, string state)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var task = await RpcAsync("tasks/get", new { id = taskId });
                if (task.GetProperty("status").GetProperty("state").GetString() == state)
                    return task;
                await Task.Delay(50);
            }
            var last = await RpcAsync("tasks/get", new { id = taskId });
            throw new TimeoutException($"task {taskId} did not reach state {state}: {last.GetRawText()}");
        }

        public void Dispose()
        {
            Client.Dispose();
            Listener.Dispose();
            Bridge.Dispose();
        }
    }

    private static object SendParams(string text, string? contextId = null, string? taskId = null)
        => new
        {
            message = new
            {
                kind = "message",
                messageId = Guid.NewGuid().ToString("N"),
                role = "user",
                parts = new[] { new { kind = "text", text } },
                contextId,
                taskId,
            },
        };

    private static async Task<JsonElement> SendAndCompleteAsync(A2aFixture fixture, string text, string? contextId = null)
    {
        var sent = await fixture.RpcAsync("message/send", SendParams(text, contextId));
        return await fixture.WaitTaskStateAsync(sent.GetProperty("id").GetString()!, "completed");
    }

    [Fact]
    public async Task AgentCard_ReturnsProtocolMetadata()
    {
        using var server = new MockDeepSeekServer(_ => SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        var response = await fixture.Client.GetAsync("/.well-known/agent-card.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1.0", string.Join("", response.Headers.GetValues("A2A-Version")));
        using var card = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = card.RootElement;
        Assert.Equal("1.0", root.GetProperty("protocolVersion").GetString());
        Assert.Equal("JSONRPC", root.GetProperty("preferredTransport").GetString());
        Assert.Equal("deepseek-harness", root.GetProperty("name").GetString());
        Assert.True(root.GetProperty("capabilities").GetProperty("streaming").GetBoolean());
        Assert.False(root.GetProperty("capabilities").GetProperty("pushNotifications").GetBoolean());
        Assert.Equal("coding", root.GetProperty("skills")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task SendGetList_CompletesFullChain()
    {
        using var server = new MockDeepSeekServer(_ => SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        var sent = await fixture.RpcAsync("message/send", SendParams("hi"));
        var taskId = sent.GetProperty("id").GetString()!;
        var contextId = sent.GetProperty("contextId").GetString()!;
        var task = await fixture.WaitTaskStateAsync(taskId, "completed");
        Assert.Equal(contextId, task.GetProperty("contextId").GetString());
        Assert.Equal("Hello world", task.GetProperty("artifacts")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        var history = task.GetProperty("history").EnumerateArray().ToList();
        Assert.Contains(history, message => message.GetProperty("role").GetString() == "user");

        var second = await SendAndCompleteAsync(fixture, "again", contextId);
        Assert.Equal(contextId, second.GetProperty("contextId").GetString());
        Assert.NotEqual(taskId, second.GetProperty("id").GetString());

        var pageOne = await fixture.RpcAsync("tasks/list", new { contextId, pageSize = 1 });
        Assert.Single(pageOne.GetProperty("tasks").EnumerateArray());
        var nextPageToken = pageOne.GetProperty("nextPageToken").GetString()!;
        Assert.NotEqual("", nextPageToken);
        var pageTwo = await fixture.RpcAsync("tasks/list", new { contextId, pageSize = 1, pageToken = nextPageToken });
        Assert.Single(pageTwo.GetProperty("tasks").EnumerateArray());
        Assert.Equal("", pageTwo.GetProperty("nextPageToken").GetString());

        var filtered = await fixture.RpcAsync("tasks/list", new { status = "completed" });
        Assert.Equal(2, filtered.GetProperty("totalSize").GetInt32());
    }

    [Fact]
    public async Task MessageStream_EmitsWorkingThenFinalCompleted()
    {
        using var server = new MockDeepSeekServer(_ => SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        var frames = await fixture.ReadSseAsync("message/stream", SendParams("hi"));
        Assert.True(frames.Count >= 2, $"expected at least 2 frames, got {frames.Count}");
        Assert.Equal("task", frames[0].GetProperty("kind").GetString());
        var updates = frames.Where(frame => frame.GetProperty("kind").GetString() == "status-update").ToList();
        Assert.Contains(updates, frame => frame.GetProperty("status").GetProperty("state").GetString() == "working");
        var final = frames[^1];
        Assert.Equal("status-update", final.GetProperty("kind").GetString());
        Assert.True(final.GetProperty("final").GetBoolean());
        Assert.Equal("completed", final.GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Resubscribe_ReplaysEventsToFinal()
    {
        using var server = new MockDeepSeekServer(_ => SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        var completed = await SendAndCompleteAsync(fixture, "hi");
        var frames = await fixture.ReadSseAsync("tasks/resubscribe", new { id = completed.GetProperty("id").GetString() });
        Assert.True(frames.Count >= 2, $"expected replay frames, got {frames.Count}");
        var final = frames[^1];
        Assert.Equal("status-update", final.GetProperty("kind").GetString());
        Assert.True(final.GetProperty("final").GetBoolean());
        Assert.Equal("completed", final.GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Cancel_RunningTask_ThenNotCancelable()
    {
        using var server = new MockDeepSeekServer(_ => SseTextOnly, TimeSpan.FromSeconds(5));
        using var fixture = new A2aFixture(server.BaseUrl);
        var sent = await fixture.RpcAsync("message/send", SendParams("hi"));
        var taskId = sent.GetProperty("id").GetString()!;
        await fixture.WaitTaskStateAsync(taskId, "working");
        var canceled = await fixture.RpcAsync("tasks/cancel", new { id = taskId });
        Assert.Equal("canceled", canceled.GetProperty("status").GetProperty("state").GetString());
        var error = await Assert.ThrowsAsync<A2aRpcError>(() => fixture.RpcAsync("tasks/cancel", new { id = taskId }));
        Assert.Equal(-32002, error.Code);
    }

    [Fact]
    public async Task ErrorCodes_AreMapped()
    {
        using var server = new MockDeepSeekServer(_ => SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        var unknown = await Assert.ThrowsAsync<A2aRpcError>(() => fixture.RpcAsync("tasks/nonexistent", new { }));
        Assert.Equal(-32601, unknown.Code);
        var notFound = await Assert.ThrowsAsync<A2aRpcError>(() => fixture.RpcAsync("tasks/get", new { id = "nope" }));
        Assert.Equal(-32001, notFound.Code);
        var push = await Assert.ThrowsAsync<A2aRpcError>(() => fixture.RpcAsync("tasks/pushNotificationConfig/set", new { }));
        Assert.Equal(-32003, push.Code);
        var extended = await Assert.ThrowsAsync<A2aRpcError>(() => fixture.RpcAsync("agent/getExtendedAgentCard", new { }));
        Assert.Equal(-32007, extended.Code);
        var invalidParams = await Assert.ThrowsAsync<A2aRpcError>(() => fixture.RpcAsync("tasks/get", new { }));
        Assert.Equal(-32602, invalidParams.Code);
        var filePart = await Assert.ThrowsAsync<A2aRpcError>(() => fixture.RpcAsync("message/send", new
        {
            message = new
            {
                messageId = "m1",
                role = "user",
                parts = new[] { new { kind = "file", file = new { uri = "https://example.invalid/a.bin" } } },
            },
        }));
        Assert.Equal(-32005, filePart.Code);
    }

    [Fact]
    public async Task VersionHeader_MismatchIsRejected()
    {
        using var server = new MockDeepSeekServer(_ => SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        var response = await fixture.PostAsync("tasks/list", new { }, a2aVersion: "2.0");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(-32600, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task BearerToken_GatesJsonRpcButNotCard()
    {
        using var server = new MockDeepSeekServer(_ => SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl, authToken: "secret");
        var rejected = await fixture.PostAsync("tasks/list", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        var accepted = await fixture.RpcAsync("tasks/list", new { }, bearer: "secret");
        Assert.Equal(0, accepted.GetProperty("totalSize").GetInt32());
        var card = await fixture.Client.GetAsync("/.well-known/agent-card.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);
    }

    [Fact]
    public async Task TerminalTask_RejectsNewMessageOnSameTask()
    {
        using var server = new MockDeepSeekServer(_ => SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        var completed = await SendAndCompleteAsync(fixture, "hi");
        var taskId = completed.GetProperty("id").GetString()!;
        var contextId = completed.GetProperty("contextId").GetString()!;
        var error = await Assert.ThrowsAsync<A2aRpcError>(() => fixture.RpcAsync("message/send", SendParams("more", contextId, taskId)));
        Assert.Equal(-32004, error.Code);
    }

    [Fact]
    public async Task InputRequired_AskUserAnsweredByNextSend()
    {
        var calls = 0;
        using var server = new MockDeepSeekServer(_ =>
        {
            calls++;
            if (calls == 1)
            {
                const string arguments = "{\\\"questions\\\":[{\\\"id\\\":\\\"q1\\\",\\\"question\\\":\\\"Pick one\\\",\\\"options\\\":[{\\\"label\\\":\\\"Alpha\\\"},{\\\"label\\\":\\\"Beta\\\"}]}]}";
                return
                [
                    "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":null}}]}",
                    "",
                    $"data: {{\"choices\":[{{\"delta\":{{\"tool_calls\":[{{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{{\"name\":\"ask_user_question\",\"arguments\":\"{arguments}\"}}}}]}}}}]}}",
                    "",
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
                    "",
                    "data: [DONE]",
                    "",
                ];
            }
            return
            [
                """data: {"choices":[{"delta":{"content":"done"}}]}""",
                "",
                """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                "",
                "data: [DONE]",
                "",
            ];
        });
        using var fixture = new A2aFixture(server.BaseUrl);
        UserQuestionService.Register(fixture.Ctx);
        AskUserTool.Register(fixture.Ctx);

        var sent = await fixture.RpcAsync("message/send", SendParams("ask me"));
        var taskId = sent.GetProperty("id").GetString()!;
        var contextId = sent.GetProperty("contextId").GetString()!;
        var waiting = await fixture.WaitTaskStateAsync(taskId, "input-required");
        Assert.Contains("Pick one", waiting.GetProperty("status").GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString());

        var resumed = await fixture.RpcAsync("message/send", SendParams("Alpha", contextId));
        Assert.Equal(taskId, resumed.GetProperty("id").GetString());
        var completed = await fixture.WaitTaskStateAsync(taskId, "completed");
        Assert.Equal("done", completed.GetProperty("artifacts")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Contains("Alpha", server.LastRequestBody);
    }
}
