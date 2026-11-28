using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Dsh.A2A;
using Dsh.Agent;
using Dsh.AgentAdapters;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.GitInspect;
using Dsh.Host;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Sdk;
using Dsh.Subagent;
using Dsh.Subprocess;
using Dsh.Transport;
using Dsh.Workflow;
using Dsh.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using A2AClient = global::A2A.A2AClient;
using A2AMessage = global::A2A.Message;
using A2APart = global::A2A.Part;
using A2ARole = global::A2A.Role;
using CancelTaskRequest = global::A2A.CancelTaskRequest;
using GetTaskRequest = global::A2A.GetTaskRequest;
using SendMessageConfiguration = global::A2A.SendMessageConfiguration;
using SendMessageRequest = global::A2A.SendMessageRequest;
using TaskState = global::A2A.TaskState;

namespace Dsh.Tests;

public sealed class ReviewRound1ExecutionBackendsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task F5_A2aSharedSession_IsolatesTurnOutcome_AndDoesNotCancelUnrelatedActiveTurn()
    {
        var turn1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn2Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn2Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;

        var ctx = CreateScriptedLlmContext(async (_, ct) =>
        {
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
            {
                turn1Entered.TrySetResult();
                await turn1Release.Task.WaitAsync(ct);
                return "local-turn-only";
            }
            if (call == 2)
            {
                turn2Entered.TrySetResult();
                await turn2Release.Task.WaitAsync(ct);
                return "a2a-turn-only";
            }
            return $"extra-{call}";
        });

        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var sharedHandle = await agents.Create(new CreateAgentOptions(
            SessionId.Create("f5-shared-session"),
            null,
            new AgentOptions("test-provider", "test-model")), Ct);
        var sharedAgent = sharedHandle.Agent;

        using var handler = new DshAgentHandler(ctx, "test-provider", "test-model");
        using var host = new A2aHost(handler, new A2aHostOptions());
        host.Start();
        using var client = new A2AClient(new Uri(host.Endpoint));

        // Turn 1 starts from an unrelated local caller.
        sharedAgent.Followup(MessageFactory.CreateUserText("local turn"));
        await turn1Entered.Task.WaitAsync(Ct);

        // Task A targets the shared session while Turn 1 is active, then is cancelled while queued.
        var queuedTask = await client.SendMessageAsync(new SendMessageRequest
        {
            Message = UserMessage("queued a2a task", sharedAgent.Id.Value),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }, Ct);
        var canceledA2a = await client.CancelTaskAsync(new CancelTaskRequest { Id = queuedTask.Task!.Id }, Ct);
        Assert.Equal(TaskState.Canceled, canceledA2a.Status.State);

        // Turn 1 must remain uncancelled and complete normally.
        Assert.Equal(AgentStatus.Running, sharedAgent.Status);
        turn1Release.TrySetResult();
        await sharedAgent.WhenIdle().WaitAsync(Ct);
        var localAssistant = sharedAgent.Session.SnapshotEvents()
            .Select(ev => ev.Data)
            .OfType<AssistantMessagePayload>()
            .Single();
        Assert.Equal("local-turn-only", string.Concat(localAssistant.Message.Content.OfType<TextBlock>().Select(b => b.Text)));

        // Task B runs on the same shared session and must return ONLY its own turn's output.
        var secondTask = await client.SendMessageAsync(new SendMessageRequest
        {
            Message = UserMessage("second a2a task", sharedAgent.Id.Value),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }, Ct);
        await turn2Entered.Task.WaitAsync(Ct);
        turn2Release.TrySetResult();

        var completedTask = await WaitTerminalAsync(client, secondTask.Task!.Id, Ct);
        Assert.Equal(TaskState.Completed, completedTask.Status.State);
        Assert.Equal("a2a-turn-only", completedTask.Status.Message!.Parts[0].Text);
        Assert.DoesNotContain("local-turn-only", completedTask.Status.Message.Parts[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F5_A2aSharedSession_CancelQueuedA2aInInbox_AndCancelOwnedTurnKeepsQueuedInbox()
    {
        var turn1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn2Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn2Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;

        var ctx = CreateScriptedLlmContext(async (_, ct) =>
        {
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
            {
                turn1Entered.TrySetResult();
                await turn1Release.Task.WaitAsync(ct);
                return "a2a-owned-turn";
            }
            if (call == 2)
            {
                turn2Entered.TrySetResult();
                await turn2Release.Task.WaitAsync(ct);
                return "followup-preserved";
            }
            return $"extra-{call}";
        });

        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var sharedHandle = await agents.Create(new CreateAgentOptions(
            SessionId.Create("f5-keep-inbox-session"),
            null,
            new AgentOptions("test-provider", "test-model")), Ct);
        var sharedAgent = (AgentLoopAgent)sharedHandle.Agent;

        using var handler = new DshAgentHandler(ctx, "test-provider", "test-model");
        using var host = new A2aHost(handler, new A2aHostOptions());
        host.Start();
        using var client = new A2AClient(new Uri(host.Endpoint));

        var ownedTask = await client.SendMessageAsync(new SendMessageRequest
        {
            Message = UserMessage("owned turn", sharedAgent.Id.Value),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }, Ct);
        await turn1Entered.Task.WaitAsync(Ct);

        // Queue another unrelated message in Inbox while the A2A turn is running.
        sharedAgent.Followup(MessageFactory.CreateUserText("unrelated queued"));
        Assert.Single(sharedAgent.Inbox.NextTurn);

        // Cancelling the active A2A task cancels its turn with keepInbox: true.
        var canceled = await client.CancelTaskAsync(new CancelTaskRequest { Id = ownedTask.Task!.Id }, Ct);
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        turn1Release.TrySetResult();
        await sharedAgent.WhenIdle().WaitAsync(Ct);

        Assert.Single(sharedAgent.Inbox.NextTurn);
        Assert.Equal("unrelated queued", string.Concat(sharedAgent.Inbox.NextTurn[0].Content.OfType<TextBlock>().Select(b => b.Text)));
        turn2Release.TrySetResult();
    }

    [Fact]
    public async Task F6_A2aListTargets_RedactsQueryFragmentAndUserInfoCredentials()
    {
        var remotes = new Dictionary<string, A2aRemoteSettings>
        {
            ["query-auth"] = new() { Url = "http://127.0.0.1:9001/rpc?token=super-secret-token&x-api-key=secret-key#frag-secret" },
            ["userinfo-auth"] = new() { Url = "https://alice:hunter2@agent.example:8443/a2a?x-custom=val#anchor" },
            ["empty"] = new() { Url = "" },
        };
        var backend = new A2aAgentExecutionBackend(remotes);

        var targets = await backend.ListTargetsAsync(Ct);

        var queryTarget = Assert.Single(targets, t => t.TargetId == "a2a:query-auth");
        Assert.Equal("http://127.0.0.1:9001/rpc", queryTarget.Endpoint);
        Assert.DoesNotContain("super-secret-token", queryTarget.Endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", queryTarget.Endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("?", queryTarget.Endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("#", queryTarget.Endpoint, StringComparison.Ordinal);

        var userInfoTarget = Assert.Single(targets, t => t.TargetId == "a2a:userinfo-auth");
        Assert.Equal("https://agent.example:8443/a2a", userInfoTarget.Endpoint);
        Assert.DoesNotContain("alice", userInfoTarget.Endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", userInfoTarget.Endpoint, StringComparison.Ordinal);

        var emptyTarget = Assert.Single(targets, t => t.TargetId == "a2a:empty");
        Assert.Null(emptyTarget.Endpoint);
        Assert.False(emptyTarget.Available);
    }

    [Fact]
    public async Task F7_A2aCardCache_KeysByAuthHeaders_AndInvalidatesPerTarget()
    {
        var cardFetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var rpcCalls = new List<(string Route, string? Auth)>();
        var gate = new Lock();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        string? baseUrl = null;
        app.Run(async http =>
        {
            var auth = http.Request.Headers.Authorization.ToString();
            if (http.Request.Path == "/.well-known/agent-card.json")
            {
                lock (gate)
                    cardFetches[auth] = cardFetches.GetValueOrDefault(auth) + 1;
                var rpcRoute = auth == "Bearer token-a" ? $"{baseUrl}/rpc-a" : $"{baseUrl}/rpc-b";
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync($$"""
                    {
                      "name": "multi-tenant-a2a",
                      "description": "test",
                      "version": "1.0",
                      "supportedInterfaces": [{"url":"{{rpcRoute}}","protocolBinding":"JSONRPC","protocolVersion":"1.0"}],
                      "capabilities": {"streaming": false},
                      "defaultInputModes": ["text/plain"],
                      "defaultOutputModes": ["text/plain"],
                      "skills": []
                    }
                    """, Ct);
                return;
            }

            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync(Ct);
            var doc = JsonNode.Parse(body)!.AsObject();
            var id = doc["id"]!.DeepClone();
            lock (gate)
                rpcCalls.Add((http.Request.Path.Value ?? "", auth));

            var reply = $"route={http.Request.Path.Value};auth={auth}";
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject
                {
                    ["task"] = new JsonObject
                    {
                        ["id"] = $"task-{rpcCalls.Count}",
                        ["contextId"] = "ctx-1",
                        ["status"] = new JsonObject
                        {
                            ["state"] = "TASK_STATE_COMPLETED",
                            ["message"] = new JsonObject
                            {
                                ["role"] = "ROLE_AGENT",
                                ["messageId"] = $"msg-{rpcCalls.Count}",
                                ["parts"] = new JsonArray { new JsonObject { ["text"] = reply } },
                            },
                        },
                    },
                },
            };
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(response.ToJsonString(), Ct);
        });
        await app.StartAsync(Ct);
        baseUrl = app.Urls.Single();

        var client = new A2aRemoteClient(new Dictionary<string, A2aRemoteSettings>
        {
            ["tenant-a"] = new() { Url = $"{baseUrl}?token=token-a" },
            ["tenant-b"] = new() { Url = $"{baseUrl}?token=token-b" },
        });

        var resA = await client.SendAsync("tenant-a", "hi", wait: true, timeoutSeconds: 10, Ct);
        var resB = await client.SendAsync("tenant-b", "hi", wait: true, timeoutSeconds: 10, Ct);

        Assert.Equal("route=/rpc-a;auth=Bearer token-a", resA.Task!.Answer);
        Assert.Equal("route=/rpc-b;auth=Bearer token-b", resB.Task!.Answer);
        Assert.Equal(1, cardFetches["Bearer token-a"]);
        Assert.Equal(1, cardFetches["Bearer token-b"]);

        // Second call hits cache for both; invalidating tenant-a only evicts tenant-a's entry.
        client.InvalidateCardCache("tenant-a");
        _ = await client.SendAsync("tenant-a", "hi2", wait: true, timeoutSeconds: 10, Ct);
        _ = await client.SendAsync("tenant-b", "hi2", wait: true, timeoutSeconds: 10, Ct);

        Assert.Equal(2, cardFetches["Bearer token-a"]);
        Assert.Equal(1, cardFetches["Bearer token-b"]);
    }

    [Fact]
    public async Task F8_A2aHost_RejectsUnauthenticatedHostAndWrongMethodBeforeReadingBody_AndReturns413WhenOversized()
    {
        var ctx = CreateScriptedLlmContext((_, _) => Task.FromResult("ok"));
        using var handler = new DshAgentHandler(ctx, "test-provider", "test-model");
        using var host = new A2aHost(handler, new A2aHostOptions(AuthToken: "top-secret"));
        host.Start();
        var endpoint = new Uri(host.Endpoint);

        // 1. Unauthenticated oversized POST with Expect: 100-continue and NO body bytes sent -> 401 immediately.
        var unauthResponse = await SendRawHttpHeadAsync(
            endpoint,
            $"POST / HTTP/1.1\r\nHost: {endpoint.Authority}\r\nContent-Length: 500000000\r\nExpect: 100-continue\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 401 Unauthorized", unauthResponse, StringComparison.Ordinal);
        Assert.DoesNotContain("100 Continue", unauthResponse, StringComparison.Ordinal);

        // 2. Unexpected Host header -> 403 before body.
        var badHostResponse = await SendRawHttpHeadAsync(
            endpoint,
            "POST / HTTP/1.1\r\nHost: evil.example:1234\r\nAuthorization: Bearer top-secret\r\nContent-Length: 500000000\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403 Forbidden", badHostResponse, StringComparison.Ordinal);

        // 3. Unknown GET path -> 404, unsupported method -> 405 before body.
        var notFoundResponse = await SendRawHttpHeadAsync(
            endpoint,
            $"GET /unknown HTTP/1.1\r\nHost: {endpoint.Authority}\r\nAuthorization: Bearer top-secret\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404 Not Found", notFoundResponse, StringComparison.Ordinal);

        var methodNotAllowedResponse = await SendRawHttpHeadAsync(
            endpoint,
            $"PUT / HTTP/1.1\r\nHost: {endpoint.Authority}\r\nAuthorization: Bearer top-secret\r\nContent-Length: 500000000\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 405 Method Not Allowed", methodNotAllowedResponse, StringComparison.Ordinal);

        // 4. Authenticated POST declaring > 16 MiB Content-Length -> 413 without sending/allocating body.
        var tooLargeContentLength = await SendRawHttpHeadAsync(
            endpoint,
            $"POST / HTTP/1.1\r\nHost: {endpoint.Authority}\r\nAuthorization: Bearer top-secret\r\nContent-Length: 16777217\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 413 Payload Too Large", tooLargeContentLength, StringComparison.Ordinal);

        // 5. Authenticated chunked POST declaring chunk > 16 MiB -> 413.
        var tooLargeChunked = await SendRawHttpHeadAsync(
            endpoint,
            $"POST / HTTP/1.1\r\nHost: {endpoint.Authority}\r\nAuthorization: Bearer top-secret\r\nTransfer-Encoding: chunked\r\n\r\n1000001\r\n");
        Assert.StartsWith("HTTP/1.1 413 Payload Too Large", tooLargeChunked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F8_HttpRequestReader_ReadsHeadThenLargeBodyIncrementally()
    {
        var payload = new string('A', 200_000);
        var raw = Encoding.ASCII.GetBytes(
            $"POST /submit?x=1 HTTP/1.1\r\nHost: localhost\r\nContent-Length: {payload.Length}\r\n\r\n{payload}");
        await using var stream = new MemoryStream(raw);

        var head = await HttpRequestReader.ReadHeadAsync(stream, Ct);
        Assert.NotNull(head);
        Assert.Equal("POST", head.Method);
        Assert.Equal("/submit", head.Path);
        Assert.Equal("x=1", head.Query);
        Assert.Equal(payload.Length, head.ContentLength);

        var body = await HttpRequestReader.ReadBodyAsync(stream, head, maxBodyBytes: 300_000, cancellationToken: Ct);
        Assert.Equal(payload.Length, body.Length);
        Assert.Equal(payload, Encoding.ASCII.GetString(body));

        await using var oversizedStream = new MemoryStream(raw);
        var oversizedHead = await HttpRequestReader.ReadHeadAsync(oversizedStream, Ct);
        await Assert.ThrowsAsync<HttpBodyTooLargeException>(
            () => HttpRequestReader.ReadBodyAsync(oversizedStream, oversizedHead!, maxBodyBytes: 100_000, cancellationToken: Ct));
    }

    [Fact]
    public async Task F10_RemoteSubagent_EnforcesMaxDepth_AndRejectsUnsupportedCapabilities()
    {
        var ctx = new Context();
        var sessions = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        _ = new LlmRuntime(ctx);
        _ = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var subagents = new SubagentRuntime(ctx);
        using var spawnReg = SubagentInProcessProviders.RegisterSpawn(ctx);

        var router = new ControlledRouter();
        using var reg = router.RegisterBackend(new ControlledBackend(AgentBackendKind.Fleet, "fleet:worker"));
        ctx.Provide(IAgentExecutionRouter.ServiceName, router);

        var depth1SessionId = SessionId.Create("f10-parent-depth1");
        var rootId = SessionId.Create("f10-root");
        var depth1Session = sessions.Create(depth1SessionId, header: new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = depth1SessionId,
            ParentSession = rootId,
            RootSession = rootId,
            Origin = "subagent",
            SubagentProvider = "spawn",
            DelegationDepth = 1,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsSeeded = false,
        });
        var depth1Parent = new AgentLoopAgent(
            ctx.Root, depth1SessionId, new AgentOptions("test-provider", "test-model"), depth1Session, SessionRepair.LastTurnOf);

        // Over maxDepth (parent depth 1 -> child depth 2 > maxDepth 1) fails closed.
        await Assert.ThrowsAsync<SubagentDepthError>(() => subagents.StartAsync("spawn", new SubagentStartRequest
        {
            Prompt = [new TextBlock("too deep")],
            Parent = depth1Parent,
            Signal = Ct,
            MaxDepth = 1,
        }, "fleet:worker", AgentBackendKind.Fleet));

        // ToolFilter fails closed on remote dispatch.
        var filterEx = await Assert.ThrowsAsync<SubagentException>(() => subagents.StartAsync("spawn", new SubagentStartRequest
        {
            Prompt = [new TextBlock("filtered")],
            Parent = depth1Parent,
            Signal = Ct,
            ToolFilter = new ToolRestriction(Allow: ["read"]),
        }, "fleet:worker", AgentBackendKind.Fleet));
        Assert.Equal(SubagentErrorCodes.UnsupportedCapability, filterEx.Code);

        // OutputSchema fails closed on remote dispatch.
        var schemaEx = await Assert.ThrowsAsync<SubagentException>(() => subagents.StartAsync("spawn", new SubagentStartRequest
        {
            Prompt = [new TextBlock("structured")],
            Parent = depth1Parent,
            Signal = Ct,
            OutputSchema = new JsonObject { ["type"] = "object" },
        }, "fleet:worker", AgentBackendKind.Fleet));
        Assert.Equal(SubagentErrorCodes.UnsupportedCapability, schemaEx.Code);
    }

    [Fact]
    public async Task F19_RemoteSubagent_CancelAndSteerReachActiveRemoteHandle()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        _ = new LlmRuntime(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var subagents = new SubagentRuntime(ctx);
        using var spawnReg = SubagentInProcessProviders.RegisterSpawn(ctx);

        var backend = new ControlledBackend(AgentBackendKind.Fleet, "fleet:gpu");
        var router = new ControlledRouter();
        using var reg = router.RegisterBackend(backend);
        ctx.Provide(IAgentExecutionRouter.ServiceName, router);

        var parent = (await agents.Create(new CreateAgentOptions(
            SessionId.Create("f19-parent"), null, new AgentOptions("test-provider", "test-model")), Ct)).Agent;

        var run = await subagents.StartAsync("spawn", new SubagentStartRequest
        {
            Prompt = [new TextBlock("long remote work")],
            Parent = parent,
            Signal = Ct,
            Background = true,
        }, "fleet:gpu", AgentBackendKind.Fleet);

        var handle = await backend.DispatchedHandle.Task.WaitAsync(Ct);
        Assert.False(backend.LastRequest!.WaitForCompletion);

        // Steer reaches the active remote handle through ObservedRun unwrapping.
        Assert.True(subagents.InjectChild(parent.Id, run.Id, [new TextBlock("focus on tests")]));
        var steered = await handle.SteerReceived.Task.WaitAsync(Ct);
        Assert.Equal("focus on tests", steered);

        // Cancel reaches the active remote handle and settles the remote run as Aborted.
        Assert.True(subagents.CancelChild(parent.Id, run.Id));
        await handle.Cancelled.Task.WaitAsync(Ct);
        var result = await run.Result.WaitAsync(Ct);
        Assert.Equal(SubagentStopReason.Aborted, result.StopReason);
    }

    [Fact]
    public async Task F19_FleetBackend_NonBlockingPollReceivesOutput_AndCancelWaitInterruptsRemote()
    {
        var spec = new MachineSpec("gpu-1", MachineKinds.Ssh, "10.0.0.10", 22, "dev", SshAuth.Agent);
        var host = new DelayedFleetHost();
        using var fleet = new FleetAgentExecutionBackend(
            new MemoryCatalog(spec),
            () => new MemoryFleetClient(new MemoryFleetMachine(spec, host)));

        // Non-blocking dispatch: GetStatusAsync stays incomplete until turn events arrive, then returns real Output.
        await using var nonBlocking = await fleet.DispatchAsync(new AgentDispatchRequest(
            TargetId: "fleet:gpu-1",
            Prompt: "compute",
            WaitForCompletion: false), Ct);

        var pendingSnap = await nonBlocking.GetStatusAsync(Ct);
        Assert.False(pendingSnap.Completed);

        host.EmitTurnCompletion(nonBlocking.SessionId!.Value.Value, "computed-result");
        var doneSnap = await nonBlocking.GetStatusAsync(Ct);
        Assert.True(doneSnap.Completed);
        Assert.Equal("computed-result", doneSnap.Output);

        // Blocking dispatch cancelled mid-wait triggers best-effort CancelAsync on remote host.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var blockingTask = fleet.DispatchAsync(new AgentDispatchRequest(
            TargetId: "fleet:gpu-1",
            Prompt: "slow-job",
            WaitForCompletion: true), cts.Token);
        await host.SubscribeEntered.Task.WaitAsync(Ct);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockingTask);
        Assert.NotEmpty(host.InterruptedSessions);
    }

    [Fact]
    public async Task F20_AcpExecutionBackend_HonorsWaitAndReturnsRealTranscriptOutputInsteadOfTitle()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell script as the ACP peer");

        await using var env = await GuiTestEnvironment.CreateIsolatedAsync();
        await using var hostBackend = await HarnessRemoteHostBackend.CreateAsync(env.App, HostPtyDaemonForTests.Unused, Ct);
        var router = env.App.Ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName)!;

        var scriptDir = Path.Combine(TempTree.RepositoryRoot, "tests/.scratch/test-tmp", $"f20-acp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir, "acp-peer.sh");
        try
        {
            await File.WriteAllTextAsync(scriptPath, """
                #!/bin/sh
                while IFS= read -r line; do
                  id=${line#*\"id\":}
                  id=${id%%,*}
                  id=${id%%\}*}
                  case "$line" in
                    *'"method":"initialize"'*)
                      printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":1,"agentCapabilities":{}}}\n' "$id"
                      ;;
                    *'"method":"session/new"'*)
                      printf '{"jsonrpc":"2.0","id":%s,"result":{"sessionId":"f20-acp-sess"}}\n' "$id"
                      ;;
                    *'"method":"session/prompt"'*)
                      sleep 0.15
                      if echo "$line" | grep -q "turn-two"; then
                        printf '{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"f20-acp-sess","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"second-acp-output"}}}}\n'
                      else
                        printf '{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"f20-acp-sess","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"real-acp-output"}}}}\n'
                      fi
                      printf '{"jsonrpc":"2.0","id":%s,"result":{"stopReason":"end_turn"}}\n' "$id"
                      ;;
                  esac
                done
                """, Ct);

            await using var handle = await router.DispatchAsync(new AgentDispatchRequest(
                PreferredBackend: AgentBackendKind.Acp,
                Prompt: "turn-one",
                Cwd: scriptDir,
                Name: "Misleading Session Title",
                WaitForCompletion: true,
                Metadata: new Dictionary<string, string>
                {
                    ["executable"] = "/bin/sh",
                    ["arguments"] = scriptPath,
                }), Ct);

            var snap1 = await handle.GetStatusAsync(Ct);
            Assert.True(snap1.Completed);
            Assert.Equal("real-acp-output", snap1.Output);
            Assert.Equal("real-acp-output", snap1.Summary);
            Assert.DoesNotContain("Misleading Session Title", snap1.Output ?? "", StringComparison.Ordinal);

            var snap2 = await handle.SendAsync("turn-two", cancellationToken: Ct);
            Assert.True(snap2.Completed);
            Assert.Equal("second-acp-output", snap2.Output);
            Assert.Equal("second-acp-output", snap2.Summary);
        }
        finally
        {
            if (Directory.Exists(scriptDir))
                Directory.Delete(scriptDir, recursive: true);
        }
    }

    [Fact]
    public async Task F20_WorkflowAndSubagent_PollUntilCompleted_AndDoNotTreatSummaryAsOutput()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        _ = new LlmRuntime(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        _ = ApprovalService.Register(ctx);
        var subagents = new SubagentRuntime(ctx);
        using var subprocess = new SubprocessService(ctx);
        using var spawn = SubagentInProcessProviders.RegisterSpawn(ctx);

        var pollingBackend = new PollingTitleBackend(pollsBeforeComplete: 2, finalOutput: "polled-real-output");
        var router = new ControlledRouter();
        using var reg = router.RegisterBackend(pollingBackend);
        ctx.Provide(IAgentExecutionRouter.ServiceName, router);

        var parent = (await agents.Create(new CreateAgentOptions(
            SessionId.Create("f20-workflow-parent"), null, new AgentOptions("test-provider", "test-model")), Ct)).Agent;

        // Subagent polls until Completed == true and returns actual Output (not Summary title).
        var subRun = await subagents.StartAsync("spawn", new SubagentStartRequest
        {
            Prompt = [new TextBlock("run acp")],
            Parent = parent,
            Signal = Ct,
        }, "acp:poller", AgentBackendKind.Acp);
        var subRes = await subRun.Result.WaitAsync(Ct);
        Assert.Equal(SubagentStopReason.Completed, subRes.StopReason);
        Assert.Equal("polled-real-output", Assert.Single(subRes.Output.OfType<TextBlock>()).Text);

        // Workflow polls until Completed == true and returns actual Output (not Summary title).
        var workflow = new WorkerThreadWorkflowEngine(ctx, null);
        var wfRun = workflow.Start(new WorkflowStartRequest
        {
            Script = """
                return await agent("run acp", new JsonObject { ["target"] = "acp:poller" });
                """,
            Meta = new Dictionary<string, object?> { ["name"] = "poll-test", ["description"] = "poll test" },
            Parent = parent,
        });
        var wfRes = await wfRun.Result.WaitAsync(Ct);
        Assert.Equal(WorkflowStopReason.Completed, wfRes.StopReason);
        Assert.Equal("polled-real-output", wfRes.Value);

        // When Output is null and only Summary is populated, neither workflow nor subagent falls back to Summary.
        var summaryOnlyBackend = new PollingTitleBackend(pollsBeforeComplete: 0, finalOutput: null);
        using var reg2 = router.RegisterBackend(summaryOnlyBackend);
        var emptySubRun = await subagents.StartAsync("spawn", new SubagentStartRequest
        {
            Prompt = [new TextBlock("summary only")],
            Parent = parent,
            Signal = Ct,
        }, "acp:summary-only", AgentBackendKind.Acp);
        var emptySubRes = await emptySubRun.Result.WaitAsync(Ct);
        Assert.Empty(emptySubRes.Output);
    }

    [Fact]
    public async Task F21_FleetBackend_UsesAcpModeForExternalPresets_AndRejectsTerminalSessions()
    {
        var spec = new MachineSpec("remote-box", MachineKinds.Ssh, "10.0.0.20", 22, "dev", SshAuth.Agent);
        var agentHost = new AgentCapableFleetHost();
        using var fleet = new FleetAgentExecutionBackend(
            new MemoryCatalog(spec),
            () => new MemoryFleetClient(new MemoryFleetMachine(spec, agentHost)));

        // 1. Non-dsharp preset must pass Mode = RemoteAgentModes.Acp.
        await using var acpHandle = await fleet.DispatchAsync(new AgentDispatchRequest(
            TargetId: "fleet:remote-box",
            Prompt: "hello codex",
            AgentPreset: "codex",
            WaitForCompletion: true), Ct);

        var startReq = Assert.Single(agentHost.StartRequests);
        Assert.Equal("codex", startReq.AgentKind);
        Assert.Equal(RemoteAgentModes.Acp, startReq.Mode);
        Assert.Equal("acp-reply:hello codex", (await acpHandle.GetStatusAsync(Ct)).Output);

        // 2. Terminal preset is rejected immediately with NotSupportedException.
        await Assert.ThrowsAsync<NotSupportedException>(() => fleet.DispatchAsync(new AgentDispatchRequest(
            TargetId: "fleet:remote-box",
            Prompt: "run tui",
            AgentPreset: "terminal"), Ct));

        // 3. If host returns HostSessionKinds.External, Fleet stops it and throws NotSupportedException.
        agentHost.ForceReturnedKind = HostSessionKinds.External;
        await Assert.ThrowsAsync<NotSupportedException>(() => fleet.DispatchAsync(new AgentDispatchRequest(
            TargetId: "fleet:remote-box",
            Prompt: "bad external",
            AgentPreset: "custom-external"), Ct));
        Assert.NotEmpty(agentHost.StoppedSessions);
        agentHost.ForceReturnedKind = null;

        // 4. AttachAsync skips HostSessionKinds.External PTY sessions.
        var ptyOnlyAgentId = AgentId.Create("pty-only-agent");
        agentHost.AddSummary(new RemoteSessionSummary(
            ptyOnlyAgentId.Value,
            HostSessionKinds.External,
            "codex",
            "/tmp",
            "PTY codex",
            HostSessionStatuses.Running,
            0,
            0,
            0,
            null));
        Assert.Null(await fleet.AttachAsync(ptyOnlyAgentId, "fleet:remote-box", Ct));
    }

    private static Context CreateScriptedLlmContext(Func<GenerateOptions, CancellationToken, Task<string>> respond)
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        var llm = new LlmRuntime(ctx);
        _ = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        llm.RegisterAdapter(["test-provider"], new AsyncScriptedAdapter("test-provider", respond));
        return ctx;
    }

    private static A2AMessage UserMessage(string text, string? contextId = null) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        ContextId = contextId,
        Role = A2ARole.User,
        Parts = [A2APart.FromText(text)],
    };

    private static async Task<global::A2A.AgentTask> WaitTerminalAsync(A2AClient client, string taskId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var task = await client.GetTaskAsync(new GetTaskRequest { Id = taskId }, cancellationToken);
            if (task.Status.State is TaskState.Completed or TaskState.Failed or TaskState.Canceled)
                return task;
            await Task.Delay(25, cancellationToken);
        }
        throw new TimeoutException($"task {taskId} did not reach a terminal state");
    }

    private static async Task<string> SendRawHttpHeadAsync(Uri endpoint, string rawRequest)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(endpoint.Host, endpoint.Port, Ct);
        await using var stream = tcp.GetStream();
        var bytes = Encoding.ASCII.GetBytes(rawRequest);
        await stream.WriteAsync(bytes, Ct);
        await stream.FlushAsync(Ct);

        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private sealed class AsyncScriptedAdapter(
        string provider,
        Func<GenerateOptions, CancellationToken, Task<string>> respond) : LlmAdapter
    {
        public override LlmProviderInfo ProviderInfo { get; } = new(provider, provider);
        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override async IAsyncEnumerable<StreamChunk> Stream(
            GenerateOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var text = await respond(options, cancellationToken);
            yield return new StreamChunk.BlockStart(0, "text");
            yield return new StreamChunk.TextDelta(0, text);
            yield return new StreamChunk.BlockEnd(0, new TextBlock(text));
            yield return new StreamChunk.Finish(new FinishReason.Stop());
        }
    }

    private sealed class ControlledRouter : IAgentExecutionRouter
    {
        private readonly List<IAgentExecutionBackend> _backends = [];
        private readonly Lock _sync = new();

        public IDisposable RegisterBackend(IAgentExecutionBackend backend)
        {
            lock (_sync)
                _backends.Add(backend);
            return new DisposeHook(() =>
            {
                lock (_sync)
                    _backends.Remove(backend);
            });
        }

        public async Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
        {
            IAgentExecutionBackend[] copy;
            lock (_sync)
                copy = [.. _backends];
            List<RemoteExecutionTarget> all = [];
            foreach (var b in copy)
                all.AddRange(await b.ListTargetsAsync(cancellationToken));
            return all;
        }

        public Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
        {
            IAgentExecutionBackend? match;
            lock (_sync)
                match = _backends.FirstOrDefault(b => b.CanHandle(request));
            return match is null
                ? throw new InvalidOperationException($"no backend matched {request.TargetId}")
                : match.DispatchAsync(request, cancellationToken);
        }

        public async Task<IAgentExecutionHandle?> AttachAsync(
            AgentId agentId,
            AgentBackendKind? preferredBackend = null,
            string? targetId = null,
            CancellationToken cancellationToken = default)
        {
            IAgentExecutionBackend[] copy;
            lock (_sync)
                copy = [.. _backends];
            foreach (var b in copy)
            {
                if (preferredBackend is { } kind && b.Kind != kind)
                    continue;
                var h = await b.AttachAsync(agentId, targetId, cancellationToken);
                if (h is not null)
                    return h;
            }
            return null;
        }

        private sealed class DisposeHook(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class ControlledBackend(AgentBackendKind kind, string targetId) : IAgentExecutionBackend
    {
        public AgentBackendKind Kind { get; } = kind;
        public AgentDispatchRequest? LastRequest { get; private set; }
        public TaskCompletionSource<ControlledHandle> DispatchedHandle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteExecutionTarget>>([new RemoteExecutionTarget(targetId, Kind, targetId)]);

        public bool CanHandle(AgentDispatchRequest request)
            => string.Equals(request.TargetId, targetId, StringComparison.OrdinalIgnoreCase);

        public Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var handle = new ControlledHandle(
                request.AgentId ?? AgentId.New(),
                request.SessionId ?? SessionId.Create($"ctrl-{Guid.NewGuid():N}"),
                Kind,
                targetId);
            DispatchedHandle.TrySetResult(handle);
            return Task.FromResult<IAgentExecutionHandle>(handle);
        }

        public Task<IAgentExecutionHandle?> AttachAsync(AgentId agentId, string? targetIdOverride = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentExecutionHandle?>(null);

        public sealed class ControlledHandle(AgentId agentId, SessionId sessionId, AgentBackendKind kind, string targetId) : IAgentExecutionHandle
        {
            public TaskCompletionSource<string> SteerReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public AgentId AgentId { get; } = agentId;
            public SessionId? SessionId { get; } = sessionId;
            public AgentBackendKind BackendKind { get; } = kind;
            public string TargetId { get; } = targetId;

            public Task<AgentExecutionSnapshot> SendAsync(
                string message,
                AgentMessageIntent intent = AgentMessageIntent.Task,
                MessagePrincipal? sender = null,
                CancellationToken cancellationToken = default)
            {
                if (intent == AgentMessageIntent.Steer)
                    SteerReceived.TrySetResult(message);
                return GetStatusAsync(cancellationToken);
            }

            public Task<AgentExecutionSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new AgentExecutionSnapshot(
                    AgentId, SessionId, BackendKind, TargetId, AgentLifecycleState.Active, Completed: false));

            public Task CancelAsync(CancellationToken cancellationToken = default)
            {
                Cancelled.TrySetResult();
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class PollingTitleBackend(int pollsBeforeComplete, string? finalOutput) : IAgentExecutionBackend
    {
        private readonly string _targetId = finalOutput is null ? "acp:summary-only" : "acp:poller";

        public AgentBackendKind Kind => AgentBackendKind.Acp;

        public Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteExecutionTarget>>([new RemoteExecutionTarget(_targetId, Kind, "Poller")]);

        public bool CanHandle(AgentDispatchRequest request)
            => string.Equals(request.TargetId, _targetId, StringComparison.OrdinalIgnoreCase);

        public Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentExecutionHandle>(new PollingHandle(
                request.AgentId ?? AgentId.New(),
                SessionId.Create($"poll-{Guid.NewGuid():N}"),
                _targetId,
                pollsBeforeComplete,
                finalOutput));

        public Task<IAgentExecutionHandle?> AttachAsync(AgentId agentId, string? targetId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentExecutionHandle?>(null);

        private sealed class PollingHandle(
            AgentId agentId,
            SessionId sessionId,
            string targetId,
            int pollsBeforeComplete,
            string? finalOutput) : IAgentExecutionHandle
        {
            private int _polls;

            public AgentId AgentId { get; } = agentId;
            public SessionId? SessionId { get; } = sessionId;
            public AgentBackendKind BackendKind => AgentBackendKind.Acp;
            public string TargetId { get; } = targetId;

            public Task<AgentExecutionSnapshot> SendAsync(
                string message,
                AgentMessageIntent intent = AgentMessageIntent.Task,
                MessagePrincipal? sender = null,
                CancellationToken cancellationToken = default) => GetStatusAsync(cancellationToken);

            public Task<AgentExecutionSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
            {
                var count = Interlocked.Increment(ref _polls);
                var done = count > pollsBeforeComplete;
                return Task.FromResult(new AgentExecutionSnapshot(
                    AgentId,
                    SessionId,
                    AgentBackendKind.Acp,
                    TargetId,
                    done ? AgentLifecycleState.Idle : AgentLifecycleState.Active,
                    Summary: done ? (finalOutput ?? "Misleading Session Title") : "Misleading Session Title",
                    Completed: done,
                    Output: done ? finalOutput : null));
            }

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryCatalog(params MachineSpec[] machines) : IMachineCatalog
    {
        private readonly FleetConfig _config = new([.. machines], new DeploySettings());
        public string FilePath => "memory://fleet";
        public FleetConfig Load() => _config;
        public void Save(FleetConfig config) { }
        public bool Remove(string name) => false;
        public void Validate(IReadOnlyList<MachineSpec> list) { }
    }

    private sealed class MemoryFleetClient(params IWorkspaceMachine[] machines) : IWorkspaceClient
    {
        public Task WhenDisposed => Task.CompletedTask;
        public event Action? Changed;
        public IReadOnlyList<IWorkspaceMachine> Machines => machines;
        public IWorkspaceMachine? Find(string name) => machines.FirstOrDefault(m => m.Machine.Name == name);
        public void Start() => Changed?.Invoke();
        public Task WaitSettledAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public IReadOnlyList<FleetMachineStatus> Statuses() => [];
        public IReadOnlyList<FleetSession> Sessions() => [];
        public IReadOnlyList<FleetMachineGroup> Groups() => [];
        public IReadOnlyList<FleetPendingItem> Pending() => [];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryFleetMachine(MachineSpec spec, IWorkspaceHost host) : IWorkspaceMachine
    {
        public MachineSpec Machine { get; } = spec;
        public MachineConnectionState State => MachineConnectionState.Connected;
        public string? Detail => null;
        public HostInfo? Info => new(HostProtocol.Version, "1.0.0", "linux", "/tmp");
        public HostHelloResponse? Hello => host.Hello;
        public IReadOnlyList<RemoteSessionSummary> Sessions => [];
        public RemotePendingList Pending => new([], []);
        public RemoteNotifyPage? Notifications => null;
        public int UnreadNotifications => 0;
        public IWorkspaceMachineNetwork Network => throw new NotSupportedException();
        public event Action<IWorkspaceMachine>? StateChanged { add { } remove { } }
        public event Action<IWorkspaceMachine>? SessionsChanged { add { } remove { } }
        public event Action<IWorkspaceMachine>? PendingChanged { add { } remove { } }
        public event Action<IWorkspaceMachine, RemoteNotificationChange?>? NotificationsChanged { add { } remove { } }
        public void Start() { }
        public Task WaitSettledAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<WorkspaceHostLease> WaitConnectedAsync(CancellationToken cancellationToken)
            => Task.FromResult(new WorkspaceHostLease(host, cancellationToken, Task.CompletedTask));
        public IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
            => host.SubscribeAsync(sessionId, fromSeq, cancellationToken);
        public Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken,
            Func<IWorkspaceHost, CancellationToken, Task>? prepare = null) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DelayedFleetHost : IWorkspaceHost
    {
        private readonly Dictionary<string, List<SessionEvent>> _events = new(StringComparer.Ordinal);
        private readonly List<RemoteSessionSummary> _summaries = [];

        public TaskCompletionSource SubscribeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> InterruptedSessions { get; } = [];
        public HostHelloResponse Hello { get; } = new(true, HostProtocol.Version, "1.0.0", null, []);
        public bool Supports(string capability) => false;
        public Task<long> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HostInfo(HostProtocol.Version, "1.0.0", "linux", "/tmp"));
        public IWorkspaceAgents Agent => throw new NotSupportedException();
        public T? GetCapability<T>() where T : class => null;
        public IWorkspaceFiles Files => throw new NotSupportedException();
        public IWorkspaceGit Git => throw new NotSupportedException();
        public IWorkspaceNotifications Notifications => throw new NotSupportedException();
        public IWorkspaceSessionEditing SessionEditing => throw new NotSupportedException();
        public IWorkspacePlugins Plugins => throw new NotSupportedException();
        public IWorkspaceNetwork Network => throw new NotSupportedException();
        public IWorkspaceTerminals Pty => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteSessionSummary>>([.. _summaries]);

        public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken = default)
        {
            var id = $"delayed-{Guid.NewGuid():N}";
            var summary = new RemoteSessionSummary(id, cwd, HostSessionStatuses.Running, 0, id, null, 0);
            _summaries.Add(summary);
            _events[id] = [];
            return Task.FromResult(summary);
        }

        public Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_summaries.First(s => s.SessionId == sessionId));

        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            var list = _events[sessionId];
            return Task.FromResult(new RemoteSessionSnapshot(
                new SessionHeader { Version = 0, Id = SessionId.Create(sessionId), CreatedAt = 0, IsSeeded = false },
                [.. list],
                list.Count));
        }

        public async IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(
            string sessionId,
            long fromSeq,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SubscribeEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void EmitTurnCompletion(string sessionId, string text)
        {
            var list = _events[sessionId];
            var seq = list.Count;
            var assistant = new AssistantMessagePayload(1, 1, MessageFactory.CreateAssistantMessage([new TextBlock(text)], "p", "m"));
            var turnEnd = new TurnEndPayload(1, new TurnEndReason.Completed());
            list.Add(new SessionEvent { Type = assistant.Type, Seq = seq, Time = 0, Data = assistant });
            list.Add(new SessionEvent { Type = turnEnd.Type, Seq = seq + 1, Time = 0, Data = turnEnd });
            var idx = _summaries.FindIndex(s => s.SessionId == sessionId);
            if (idx >= 0)
                _summaries[idx] = _summaries[idx] with { Status = HostSessionStatuses.Idle };
        }

        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            InterruptedSessions.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public IAsyncEnumerable<RemotePendingChange> PendingChangesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default, bool rememberForSession = false, bool cancelled = false) => Task.FromResult(false);
        public Task<bool> RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default, string status = "answered", string? reason = null) => Task.FromResult(false);
        public Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RemotePendingList([], []));
        public Task<IReadOnlyList<RemoteSessionSummary>> ListHostSessionsAsync(CancellationToken cancellationToken = default) => ListSessionsAsync(cancellationToken);
        public IAsyncEnumerable<RemoteSessionChange> HostSessionChangesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AgentCapableFleetHost : IWorkspaceHost, IWorkspaceAgents
    {
        private readonly Dictionary<string, List<SessionEvent>> _events = new(StringComparer.Ordinal);
        private readonly List<RemoteSessionSummary> _summaries = [];

        public List<RemoteAgentStartRequest> StartRequests { get; } = [];
        public List<string> StoppedSessions { get; } = [];
        public string? ForceReturnedKind { get; set; }
        public HostHelloResponse Hello { get; } = new(true, HostProtocol.Version, "1.0.0", null, [AgentProtocol.Capability]);
        public bool Supports(string capability) => string.Equals(capability, AgentProtocol.Capability, StringComparison.Ordinal);
        public IWorkspaceAgents Agent => this;
        public T? GetCapability<T>() where T : class => this as T;
        public Task<long> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HostInfo(HostProtocol.Version, "1.0.0", "linux", "/tmp"));
        public IWorkspaceFiles Files => throw new NotSupportedException();
        public IWorkspaceGit Git => throw new NotSupportedException();
        public IWorkspaceNotifications Notifications => throw new NotSupportedException();
        public IWorkspaceSessionEditing SessionEditing => throw new NotSupportedException();
        public IWorkspacePlugins Plugins => throw new NotSupportedException();
        public IWorkspaceNetwork Network => throw new NotSupportedException();
        public IWorkspaceTerminals Pty => throw new NotSupportedException();

        public void AddSummary(RemoteSessionSummary summary) => _summaries.Add(summary);

        public Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteAgentCatalogEntry>>([]);

        public Task<RemoteSessionSummary> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken = default)
        {
            StartRequests.Add(request);
            var id = $"acp-{Guid.NewGuid():N}";
            var kind = ForceReturnedKind ?? (request.Mode == RemoteAgentModes.Acp ? HostSessionKinds.Acp : HostSessionKinds.External);
            var summary = new RemoteSessionSummary(
                id,
                kind,
                request.AgentKind,
                request.ProjectPath,
                request.Title,
                HostSessionStatuses.Idle,
                0,
                0,
                0,
                null);
            _summaries.Add(summary);
            var list = new List<SessionEvent>();
            if (request.InitialPrompt is { Length: > 0 } prompt)
            {
                var assistant = new AssistantMessagePayload(1, 1, MessageFactory.CreateAssistantMessage([new TextBlock($"acp-reply:{prompt}")], "p", "m"));
                var turnEnd = new TurnEndPayload(1, new TurnEndReason.Completed());
                list.Add(new SessionEvent { Type = assistant.Type, Seq = 0, Time = 0, Data = assistant });
                list.Add(new SessionEvent { Type = turnEnd.Type, Seq = 1, Time = 0, Data = turnEnd });
            }
            _events[id] = list;
            return Task.FromResult(summary);
        }

        public Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            StoppedSessions.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task<RemoteSessionSummary> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_summaries.First(s => s.SessionId == sessionId));

        public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteSessionSummary>>([.. _summaries]);

        public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            var list = _events.TryGetValue(sessionId, out var existing) ? existing : [];
            return Task.FromResult(new RemoteSessionSnapshot(
                new SessionHeader { Version = 0, Id = SessionId.Create(sessionId), CreatedAt = 0, IsSeeded = false },
                [.. list],
                list.Count));
        }

        public async IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(
            string sessionId,
            long fromSeq,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var ev in _events[sessionId].Where(e => e.Seq >= fromSeq))
                yield return new RemoteSessionEvent(sessionId, ev);
        }

        public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public IAsyncEnumerable<RemotePendingChange> PendingChangesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default, bool rememberForSession = false, bool cancelled = false) => Task.FromResult(false);
        public Task<bool> RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default, string status = "answered", string? reason = null) => Task.FromResult(false);
        public Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RemotePendingList([], []));
        public Task<IReadOnlyList<RemoteSessionSummary>> ListHostSessionsAsync(CancellationToken cancellationToken = default) => ListSessionsAsync(cancellationToken);
        public IAsyncEnumerable<RemoteSessionChange> HostSessionChangesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
