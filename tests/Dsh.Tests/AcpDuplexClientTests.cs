using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Dsh.Acp;
using Dsh.Sdk;
using Dsh.Transport;

namespace Dsh.Tests;

/**
 * Protocol tests over in-process pipes.
 * The agent side is a scripted ACP peer that writes the exact JSON lines the schema prescribes; it is wire-level test input, not an LLM.
 */
public sealed class AcpDuplexClientTests : IAsyncDisposable
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);

    private readonly Pipe _toAgent = new();
    private readonly Pipe _toClient = new();
    private readonly StreamReader _agentInput;
    private readonly Stream _agentOutput;
    private readonly AcpClient _client;

    public AcpDuplexClientTests()
    {
        _agentInput = new StreamReader(_toAgent.Reader.AsStream(), Encoding.UTF8);
        _agentOutput = _toClient.Writer.AsStream();
        var transport = new JsonRpcLineTransport(_toClient.Reader.AsStream(), _toAgent.Writer.AsStream());
        _client = new AcpClient(transport);
        transport.Start();
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();

    [Fact]
    public async Task Handshake_SendsSchemaShapedRequestsAndReadsCapabilities()
    {
        var initialize = _client.InitializeAsync(new AcpImplementationInfo("dsh-tests", "1.0"), TestContext.Current.CancellationToken);
        var request = await AgentReadAsync();
        await AgentReplyAsync(request, """{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"sessionCapabilities":{"resume":{}}},"agentInfo":{"name":"scripted","version":"0.1"},"authMethods":[{"id":"login","name":"Login"}]}""");
        var info = await initialize.WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        var newSession = _client.NewSessionAsync("relative-dir", TestContext.Current.CancellationToken);
        var sessionRequest = await AgentReadAsync();
        await AgentReplyAsync(sessionRequest, """{"sessionId":"s1"}""");
        var session = await newSession.WaitAsync(StepTimeout, TestContext.Current.CancellationToken);

        var parameters = request.GetProperty("params");
        Assert.Equal(("initialize", 1), (request.GetProperty("method").GetString(), parameters.GetProperty("protocolVersion").GetInt32()));
        Assert.False(parameters.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.False(parameters.GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean());
        Assert.Equal("dsh-tests", parameters.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.Equal((1, "scripted", true, true), (info.ProtocolVersion, info.AgentInfo!.Name, info.CanLoadSession, info.CanResumeSession));
        Assert.Equal("login", Assert.Single(info.AuthMethods!).Id);
        Assert.Equal("session/new", sessionRequest.GetProperty("method").GetString());
        Assert.Equal(Path.GetFullPath("relative-dir"), sessionRequest.GetProperty("params").GetProperty("cwd").GetString());
        Assert.Equal(0, sessionRequest.GetProperty("params").GetProperty("mcpServers").GetArrayLength());
        Assert.Equal("s1", session.SessionId);
    }

    [Fact]
    public async Task Prompt_StreamsUpdatesWhilePermissionWaitsAndFirstAnswerWins()
    {
        var updates = new List<AcpSessionUpdate>();
        var toolCallSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.SessionUpdate += update =>
        {
            updates.Add(update);
            if (update.Kind == AcpMethods.UpdateToolCall)
                toolCallSeen.TrySetResult();
        };

        var prompt = _client.PromptAsync("s1", "hello", TestContext.Current.CancellationToken);
        var promptRequest = await AgentReadAsync();
        await AgentSendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hi"}}}}""");
        await AgentSendAsync(PermissionRequestLine("perm-1"));
        var permission = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        await AgentSendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"call_1","title":"touch x","status":"pending"}}}""");
        await toolCallSeen.Task.WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        var pendingWhileWaiting = _client.PendingPermissions.Count;
        var (unknownOption, selected, lateCancel) = (permission.TrySelect("nope"), permission.TrySelect("allow"), permission.TryCancel());
        var permissionResponse = await AgentReadAsync();
        await AgentReplyAsync(promptRequest, """{"stopReason":"end_turn"}""");
        var stopReason = await prompt.WaitAsync(StepTimeout, TestContext.Current.CancellationToken);

        Assert.Equal("hello", promptRequest.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal(("s1", "call_1", "touch x"), (permission.SessionId, permission.ToolCall.ToolCallId, permission.ToolCall.Title));
        Assert.Equal(["allow_once", "reject_once"], permission.Options.Select(option => option.Kind));
        Assert.Equal((1, false, true, false), (pendingWhileWaiting, unknownOption, selected, lateCancel));
        Assert.Equal("\"perm-1\"", permissionResponse.GetProperty("id").GetRawText());
        var outcome = permissionResponse.GetProperty("result").GetProperty("outcome");
        Assert.Equal(("selected", "allow"), (outcome.GetProperty("outcome").GetString(), outcome.GetProperty("optionId").GetString()));
        Assert.Equal(AcpMethods.StopEndTurn, stopReason);
        Assert.Equal([AcpMethods.UpdateAgentMessageChunk, AcpMethods.UpdateToolCall], updates.Select(update => update.Kind));
        Assert.Equal("Hi", updates[0].Text);
        Assert.Equal(("call_1", "pending"), (updates[1].ToolCallId, updates[1].Status));
        Assert.Empty(_client.PendingPermissions);
    }

    [Fact]
    public async Task Cancel_NotifiesAgentAndCancelsPendingPermissions()
    {
        var prompt = _client.PromptAsync("s1", "long task", TestContext.Current.CancellationToken);
        var promptRequest = await AgentReadAsync();
        await AgentSendAsync(PermissionRequestLine("perm-2"));
        var permission = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);

        await _client.CancelAsync("s1", TestContext.Current.CancellationToken);
        JsonElement[] received = [await AgentReadAsync(), await AgentReadAsync()];
        await AgentReplyAsync(promptRequest, """{"stopReason":"cancelled"}""");
        var stopReason = await prompt.WaitAsync(StepTimeout, TestContext.Current.CancellationToken);

        var cancel = Assert.Single(received, message => message.TryGetProperty("method", out _));
        Assert.Equal("session/cancel", cancel.GetProperty("method").GetString());
        Assert.False(cancel.TryGetProperty("id", out _));
        Assert.Equal("s1", cancel.GetProperty("params").GetProperty("sessionId").GetString());
        var answer = Assert.Single(received, message => message.TryGetProperty("result", out _));
        Assert.Equal("cancelled", answer.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.False(answer.GetProperty("result").GetProperty("outcome").TryGetProperty("optionId", out _));
        Assert.Equal(AcpMethods.OutcomeCancelled, (await permission.Outcome).Outcome);
        Assert.Equal(AcpMethods.StopCancelled, stopReason);
    }

    [Fact]
    public async Task Cancel_AnswersLatePermissionButDoesNotCancelTheNextTurn()
    {
        var prompt = _client.PromptAsync("s1", "long task", TestContext.Current.CancellationToken);
        var request = await AgentReadAsync();
        await _client.CancelAsync("s1", TestContext.Current.CancellationToken);
        Assert.Equal(AcpMethods.Cancel, (await AgentReadAsync()).GetProperty("method").GetString());

        await AgentSendAsync(PermissionRequestLine("late-permission"));
        var cancelled = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        var cancelledResponse = await AgentReadAsync();
        Assert.Equal(AcpMethods.OutcomeCancelled, cancelledResponse.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.True(cancelled.IsResolved);
        await AgentReplyAsync(request, """{"stopReason":"cancelled"}""");
        Assert.Equal(AcpMethods.StopCancelled, await prompt.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));

        var next = _client.PromptAsync("s1", "next turn", TestContext.Current.CancellationToken);
        var nextRequest = await AgentReadAsync();
        await AgentSendAsync(PermissionRequestLine("next-permission"));
        var permission = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        Assert.False(permission.IsResolved);
        Assert.True(permission.TrySelect("deny"));
        var selectedResponse = await AgentReadAsync();
        Assert.Equal(AcpMethods.OutcomeSelected, selectedResponse.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        await AgentReplyAsync(nextRequest, """{"stopReason":"end_turn"}""");
        Assert.Equal(AcpMethods.StopEndTurn, await next.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptEnd_CancelsOnlyItsPermissions_BeforeNextTurn(bool failed)
    {
        var prompt = _client.PromptAsync("s1", "first turn", TestContext.Current.CancellationToken);
        var request = await AgentReadAsync();
        await AgentSendAsync(PermissionRequestLine("old-permission"));
        var old = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        await AgentSendAsync(PermissionRequestLine("other-session", "s2"));
        var other = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        if (failed)
        {
            await AgentSendAsync($$$"""{"jsonrpc":"2.0","id":{{{request.GetProperty("id").GetRawText()}}},"error":{"code":{{{JsonRpcPeer.MethodNotFound}}},"message":"prompt is no longer available"}}""");
            await Assert.ThrowsAsync<JsonRpcResponseError>(() => prompt.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
        }
        else
        {
            await AgentReplyAsync(request, """{"stopReason":"end_turn"}""");
            Assert.Equal(AcpMethods.StopEndTurn, await prompt.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
        }
        Assert.True(old.IsResolved);
        Assert.False(other.IsResolved);
        Assert.Equal(AcpMethods.OutcomeCancelled, (await AgentReadAsync()).GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        var next = _client.PromptAsync("s1", "next turn", TestContext.Current.CancellationToken);
        var nextRequest = await AgentReadAsync();
        Assert.False(old.TrySelect("allow"));
        await AgentSendAsync(PermissionRequestLine("next-permission"));
        var current = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        Assert.True(current.TrySelect("deny"));
        Assert.Equal("next-permission", (await AgentReadAsync()).GetProperty("id").GetString());
        await AgentReplyAsync(nextRequest, """{"stopReason":"end_turn"}""");
        Assert.Equal(AcpMethods.StopEndTurn, await next.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
        Assert.True(other.TrySelect("allow"));
        Assert.Equal("other-session", (await AgentReadAsync()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task UnsupportedAgentRequest_GetsMethodNotFound()
    {
        await AgentSendAsync("""{"jsonrpc":"2.0","id":7,"method":"fs/read_text_file","params":{"sessionId":"s1","path":"/etc/hosts"}}""");

        var response = await AgentReadAsync();

        Assert.Equal("7", response.GetProperty("id").GetRawText());
        Assert.Equal(JsonRpcPeer.MethodNotFound, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task AgentExit_FailsPendingCallsAndReleasesPermissions()
    {
        var prompt = _client.PromptAsync("s1", "hello", TestContext.Current.CancellationToken);
        await AgentReadAsync();
        await AgentSendAsync(PermissionRequestLine("perm-3"));
        var permission = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);

        await _toClient.Writer.CompleteAsync();

        await Assert.ThrowsAsync<JsonRpcTransportClosedException>(() => prompt.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
        Assert.Equal(AcpMethods.OutcomeCancelled, (await permission.Outcome.WaitAsync(StepTimeout, TestContext.Current.CancellationToken)).Outcome);
        Assert.False(await _client.PermissionRequests.WaitToReadAsync(TestContext.Current.CancellationToken));
        Assert.Null(await _client.Closed.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"jsonrpc":"2.0","id":"broken"}""")]
    public async Task InvalidAgentOutput_FailsPendingCallsAndReleasesPermissions(string line)
    {
        var prompt = _client.PromptAsync("s1", "hello", TestContext.Current.CancellationToken);
        await AgentReadAsync();
        await AgentSendAsync(PermissionRequestLine("perm-invalid"));
        var permission = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);

        await AgentSendAsync(line);

        await Assert.ThrowsAnyAsync<JsonException>(() => prompt.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
        Assert.IsAssignableFrom<JsonException>(await _client.Closed.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
        Assert.Equal(AcpMethods.OutcomeCancelled, (await permission.Outcome.WaitAsync(StepTimeout, TestContext.Current.CancellationToken)).Outcome);
        Assert.False(await _client.PermissionRequests.WaitToReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownNotification_DoesNotPreventThePromptResponse()
    {
        var prompt = _client.PromptAsync("s1", "hello", TestContext.Current.CancellationToken);
        var request = await AgentReadAsync();
        await AgentSendAsync("""{"jsonrpc":"2.0","method":"extension/progress","params":{"sessionId":"s1","stage":"working"}}""");
        await AgentReplyAsync(request, """{"stopReason":"end_turn"}""");

        Assert.Equal(AcpMethods.StopEndTurn, await prompt.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
        Assert.False(_client.Closed.IsCompleted);
    }

    [Fact]
    public async Task PendingPermissions_DoNotBlockOtherIncomingRequestsOrResponses()
    {
        await AgentSendAsync(PermissionRequestLine("perm-first"));
        var first = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        await AgentSendAsync(PermissionRequestLine("perm-second"));
        var second = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);

        var newSession = _client.NewSessionAsync(Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken);
        var request = await AgentReadAsync();
        await AgentReplyAsync(request, """{"sessionId":"s2"}""");
        var session = await newSession.WaitAsync(StepTimeout, TestContext.Current.CancellationToken);

        Assert.Equal("s2", session.SessionId);
        Assert.Equal(2, _client.PendingPermissions.Count);
        Assert.True(second.TrySelect("deny"));
        var secondResponse = await AgentReadAsync();
        Assert.Equal("perm-second", secondResponse.GetProperty("id").GetString());
        Assert.False(first.IsResolved);
        Assert.True(first.TryCancel());
        var firstResponse = await AgentReadAsync();
        Assert.Equal("perm-first", firstResponse.GetProperty("id").GetString());
    }

    [Fact]
    public async Task CancelledCall_IgnoresItsLateResponseAndKeepsTheConnectionUsable()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelled = _client.PromptAsync("s1", "cancelled call", cancellation.Token);
        var firstRequest = await AgentReadAsync();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));

        var prompt = _client.PromptAsync("s2", "another call", TestContext.Current.CancellationToken);
        var secondRequest = await AgentReadAsync();
        await AgentReplyAsync(firstRequest, """{"stopReason":"cancelled"}""");
        await AgentReplyAsync(secondRequest, """{"stopReason":"end_turn"}""");

        Assert.Equal(AcpMethods.StopEndTurn, await prompt.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
        Assert.False(_client.Closed.IsCompleted);
    }

    [Fact]
    public async Task Dispose_FailsPendingPromptAndReleasesPermissions()
    {
        var prompt = _client.PromptAsync("s1", "hello", TestContext.Current.CancellationToken);
        await AgentReadAsync();
        await AgentSendAsync(PermissionRequestLine("perm-dispose"));
        var permission = await _client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);

        await _client.DisposeAsync();

        await Assert.ThrowsAsync<JsonRpcTransportClosedException>(() => prompt.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
        Assert.Equal(AcpMethods.OutcomeCancelled, (await permission.Outcome.WaitAsync(StepTimeout, TestContext.Current.CancellationToken)).Outcome);
        Assert.False(await _client.PermissionRequests.WaitToReadAsync(TestContext.Current.CancellationToken));
        Assert.Null(await _client.Closed.WaitAsync(StepTimeout, TestContext.Current.CancellationToken));
    }

    private static string PermissionRequestLine(string id, string sessionId = "s1")
        => $$$"""{"jsonrpc":"2.0","id":"{{{id}}}","method":"session/request_permission","params":{"sessionId":"{{{sessionId}}}","toolCall":{"toolCallId":"call_1","title":"touch x","kind":"execute","status":"pending"},"options":[{"optionId":"allow","name":"Allow","kind":"allow_once"},{"optionId":"deny","name":"Reject","kind":"reject_once"}]}}""";

    private async Task<JsonElement> AgentReadAsync()
    {
        var line = await _agentInput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        return JsonDocument.Parse(line ?? throw new EndOfStreamException("client closed its output")).RootElement.Clone();
    }

    private async Task AgentSendAsync(string line)
    {
        await _agentOutput.WriteAsync(Encoding.UTF8.GetBytes($"{line}\n"), TestContext.Current.CancellationToken);
        await _agentOutput.FlushAsync(TestContext.Current.CancellationToken);
    }

    private Task AgentReplyAsync(JsonElement request, string resultJson)
        => AgentSendAsync($$$"""{"jsonrpc":"2.0","id":{{{request.GetProperty("id").GetRawText()}}},"result":{{{resultJson}}}}""");
}
