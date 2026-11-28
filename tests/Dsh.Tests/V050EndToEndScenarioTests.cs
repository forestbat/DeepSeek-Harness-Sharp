using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Channels;
using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Host;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class V050EndToEndScenarioTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class LocalDeepSeekHttpServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private LocalDeepSeekHttpServer()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.Run(ServeAsync);
        }

        public int Port { get; private set; }
        public string BaseUrl => $"http://127.0.0.1:{Port}";
        public List<string> Requests { get; } = [];

        public static async Task<LocalDeepSeekHttpServer> StartAsync(CancellationToken cancellationToken)
        {
            var server = new LocalDeepSeekHttpServer();
            await server._app.StartAsync(cancellationToken);
            server.Port = new Uri(server._app.Urls.Single()).Port;
            return server;
        }

        private async Task ServeAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            lock (Requests)
                Requests.Add(body);
            context.Response.ContentType = "text/event-stream";
            var lines = new[]
            {
                """data: {"choices":[{"delta":{"role":"assistant","content":null,"reasoning_content":""}}]}""",
                "",
                """data: {"choices":[{"delta":{"content":"Assistant handled task."}}]}""",
                "",
                """data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":4,"total_tokens":16}}""",
                "",
                "data: [DONE]",
                "",
            };
            foreach (var line in lines)
            {
                var bytes = Encoding.UTF8.GetBytes($"{line}\n");
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    [Fact]
    public async Task Section10_FullComposedInfrastructure_ExecutesEndToEndScenario()
    {
        var root = TempTree.CreateDirectory("v050-e2e");
        try
        {
            await using var llmServer = await LocalDeepSeekHttpServer.StartAsync(Ct);
            var homePath = Path.Combine(root, "home");
            var projectDir = Path.Combine(root, "workspace");
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(projectDir);

            await File.WriteAllTextAsync(
                Path.Combine(homePath, "settings.yaml"),
                $$"""
                global_default_model: local/test-model
                providers:
                  local:
                    type: deepseek
                    options:
                      baseUrl: {{llmServer.BaseUrl}}
                      apiKey: test-key
                    models:
                      test-model:
                        name: Test Model
                plugins:
                  "@deepseek-ai/dsh-channels":
                    secrets:
                      "github:secret": "gh-webhook-secret"
                      "slack:signingSecret": "slack-signing-secret"
                """,
                Ct);

            var home = HarnessHome.Resolve(homePath);
            using var app = await ConfigBoot.Compose(
                new HarnessOptions(home, projectDir, Provider: "local", Model: "test-model", IsTui: false),
                global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);

            var agents = app.Ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName)!;
            var bus = app.Ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName)!;
            var router = app.Ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName)!;
            var rooms = app.Ctx.Get<ICollaborationRoomService>(ICollaborationRoomService.ServiceName)!;
            var discovery = app.Ctx.Get<IAgentDiscoveryService>(IAgentDiscoveryService.ServiceName)!;
            var channels = app.Ctx.Get<IExternalChannelRouter>(IExternalChannelRouter.ServiceName)!;
            var listener = app.Ctx.Get<ChannelWebhookListener>("channelWebhookListener")!;

            // 1. Create long-lived Personal Assistant + Coding Agent, subscribe Assistant to Coding Agent, and suspend Assistant.
            var assistant = await agents.CreateAsync(
                new AgentProfileRegistration("personal-assistant", "Human-facing coordinator", Cwd: projectDir),
                Ct);
            var coder = await agents.CreateAsync(
                new AgentProfileRegistration("gpu-coder", "Remote workstation coder", Cwd: projectDir),
                Ct);

            using var subscription = bus.Subscribe(new AgentEventSubscription(
                "sub-assistant-watches-coder",
                coder.AgentId,
                assistant.AgentId,
                [AgentRuntimeEventKind.TurnCompleted],
                WakeSubscriber: true));

            await agents.SuspendAsync(assistant.AgentId, Ct);
            Assert.Equal(AgentLifecycleState.Suspended, agents.Get(assistant.AgentId)!.State);

            // 2. Host RPC + multi-user Collaboration Room with Owner / Collaborator / Observer.
            await using var hostBackend = await HarnessRemoteHostBackend.CreateAsync(app, HostPtyDaemonForTests.Unused, Ct);
            var server = new RemoteHostServer(new RemoteHostServerOptions(Token: "owner-master-token"), hostBackend);

            var (ownerServerStream, ownerClientStream) = FullDuplexStream.CreatePair();
            var ownerServerTask = server.ServeAsync(ownerServerStream, Ct);
            await using var ownerClient = await RemoteHostClient.ConnectAsync(ownerClientStream, "owner-master-token", "owner-desktop", Ct);
            var ownerMesh = ownerClient.GetCapability<IWorkspaceMesh>()!;

            var room = await ownerMesh.CreateRoomAsync("lab-collab", new RoomPolicy(), Ct);
            await rooms.AddAgentAsync(room.Id, new MessagePrincipal(PrincipalKind.Human, room.OwnerPrincipalId, "Owner"), assistant.AgentId, "Personal Assistant", Ct);
            var collabInvite = await ownerMesh.CreateInviteAsync(room.Id.Value, RoomRole.Collaborator, cancellationToken: Ct);
            var observerInvite = await ownerMesh.CreateInviteAsync(room.Id.Value, RoomRole.Observer, cancellationToken: Ct);

            var (obsServerStream, obsClientStream) = FullDuplexStream.CreatePair();
            var obsServerTask = server.ServeAsync(obsServerStream, Ct);
            await using var observerClient = await RemoteHostClient.ConnectAsync(obsClientStream, token: null, "guest-observer", "alice", observerInvite.Token, Ct);
            Assert.Equal(nameof(RoomRole.Observer), observerClient.Hello.Role);
            var observerMesh = observerClient.GetCapability<IWorkspaceMesh>()!;
            await Assert.ThrowsAnyAsync<Exception>(() => observerMesh.PostRoomMessageAsync(room.Id.Value, "blocked write", cancellationToken: Ct));

            var (colServerStream, colClientStream) = FullDuplexStream.CreatePair();
            var colServerTask = server.ServeAsync(colServerStream, Ct);
            await using var collaboratorClient = await RemoteHostClient.ConnectAsync(colClientStream, token: null, "partner-cli", "bob", collabInvite.Token, Ct);
            Assert.Equal(nameof(RoomRole.Collaborator), collaboratorClient.Hello.Role);
            var collaboratorMesh = collaboratorClient.GetCapability<IWorkspaceMesh>()!;

            // Collaborator mentions @personal-assistant, waking the suspended Personal Assistant without creating a second AgentId.
            await collaboratorMesh.PostRoomMessageAsync(room.Id.Value, "Please check coder status", assistant.AgentId.Value, cancellationToken: Ct);
            var liveAssistant = await agents.WakeAsync(assistant.AgentId, Ct);
            await liveAssistant.WhenIdle().WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.NotEmpty(llmServer.Requests);

            // 3. Discovery != Trust: imported card starts untrusted and requires explicit grant.
            var externalCard = new DiscoveredAgentCard(
                "ext-partner-agent",
                "Partner Lab Agent",
                "Collaborator external agent",
                AgentBackendKind.A2a,
                "https://partner.example.invalid/a2a",
                ["research"],
                OwnerPrincipalId: "bob");
            var imported = discovery.ImportCard(JsonSerializer.Serialize(externalCard, DshCoreJsonContext.Default.DiscoveredAgentCard), "invite")!;
            var untrusted = discovery.GetTrust(imported.CardId);
            Assert.True(untrusted is null || untrusted.Status != AgentTrustStatus.Trusted);
            var trusted = await ownerMesh.GrantTrustAsync(imported.CardId, new AgentTrustPermissions(AllowMessaging: true), "collaborator", Ct);
            Assert.Equal(AgentTrustStatus.Trusted, trusted.Status);

            // 4. Suspend Assistant again and wake via REAL signed GitHub & Slack HTTP webhooks.
            await agents.SuspendAsync(assistant.AgentId, Ct);
            Assert.Equal(AgentLifecycleState.Suspended, agents.Get(assistant.AgentId)!.State);

            channels.Bind(new ChannelBinding(
                "slack-pa",
                "slack",
                "C_LAB_OPS",
                assistant.AgentId,
                room.Id,
                SecretRef: "slack:signingSecret"));
            channels.AddTrigger(new AutomationTriggerRule(
                "ci-watch",
                "GitHub CI",
                "github",
                "workflow_run*",
                assistant.AgentId,
                "CI event {{event}}: {{payload}}",
                Enabled: true,
                WakeAgent: true));

            var webhookPort = await listener.StartListenerAsync(0, "127.0.0.1", Ct);
            using var http = new HttpClient();

            // Unsigned GitHub webhook must be rejected (401).
            var ghPayload = """{"action":"completed","conclusion":"success"}""";
            var badResponse = await http.PostAsync(
                $"http://127.0.0.1:{webhookPort}/webhooks/github",
                new StringContent(ghPayload, Encoding.UTF8, "application/json"),
                Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, badResponse.StatusCode);

            // Signed Slack webhook wakes the suspended Personal Assistant.
            var slackPayload = """{"type":"event_callback","event":{"type":"message","channel":"C_LAB_OPS","user":"U_OWNER","text":"summarize ci","ts":"1700000000.1","thread_ts":"1700000000.1"}}""";
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var slackSig = ComputeHmacHex("slack-signing-secret", $"v0:{ts}:{slackPayload}");
            using var slackRequest = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{webhookPort}/webhooks/slack")
            {
                Content = new StringContent(slackPayload, Encoding.UTF8, "application/json"),
            };
            slackRequest.Headers.Add("X-Slack-Request-Timestamp", ts);
            slackRequest.Headers.Add("X-Slack-Signature", $"v0={slackSig}");
            var slackResponse = await http.SendAsync(slackRequest, Ct);
            Assert.Equal(HttpStatusCode.OK, slackResponse.StatusCode);

            var wokenAfterWebhook = agents.Get(assistant.AgentId)!;
            Assert.NotEqual(AgentLifecycleState.Suspended, wokenAfterWebhook.State);
            if (app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, strict: false)?.GetByAgent(assistant.AgentId) is { } wokenLoop)
                await wokenLoop.WhenIdle().WaitAsync(TimeSpan.FromSeconds(10), Ct);

            // 5. Render headless Avalonia Mesh view against live IMeshRpc client.
            await HeadlessGui.RunAsync(async () =>
            {
                using var vm = new AgentMeshViewModel();
                vm.AttachMesh(ownerMesh, "local", ownerClient);
                var view = new AgentMeshView { DataContext = vm };
                var window = new Window { Width = 1000, Height = 700, Content = view };
                window.Show();
                try
                {
                    await vm.RefreshCommand.ExecuteAsync(null);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(vm.Agents.Count >= 2);
                    Assert.Contains(vm.Rooms, item => item.Name == "lab-collab");
                    Assert.Contains(vm.Channels, item => item.BindingId == "slack-pa");

                    var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
                    for (var index = 0; index < 4; index++)
                    {
                        tabs.SelectedIndex = index;
                        window.Measure(new Size(1000, 700));
                        window.Arrange(new Rect(0, 0, 1000, 700));
                        Dispatcher.UIThread.RunJobs();
                    }
                }
                finally
                {
                    window.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            });

            // 6. Verify execution router lists local targets and rejects untrusted ExternalAgent Control intent.
            var targets = await router.ListTargetsAsync(Ct);
            Assert.Contains(targets, item => item.Kind == AgentBackendKind.Local);
            var controlReject = await bus.SendAsync(new AgentEnvelope(
                Guid.NewGuid().ToString("N"),
                coder.AgentId,
                null,
                new MessagePrincipal(PrincipalKind.Agent, "unknown-external", "External"),
                MessageTrustLevel.ExternalAgent,
                AgentMessageIntent.Control,
                "override"), Ct);
            Assert.Equal(AgentDeliveryStatus.Rejected, controlReject.Status);

            ownerClientStream.Dispose();
            obsClientStream.Dispose();
            colClientStream.Dispose();
            await Task.WhenAll(ownerServerTask, obsServerTask, colServerTask);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public async Task SecurityBoundaries_EnforceExternalAgentAndRemoteRoleIsolation()
    {
        var root = TempTree.CreateDirectory("v050-sec");
        try
        {
            var homePath = Path.Combine(root, "home");
            var projectDir = Path.Combine(root, "workspace");
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(projectDir);
            await File.WriteAllTextAsync(Path.Combine(homePath, "settings.yaml"), "rules: []\n", Ct);

            using var app = await ConfigBoot.Compose(
                new HarnessOptions(HarnessHome.Resolve(homePath), projectDir, IsTui: false),
                global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);

            var agents = app.Ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName)!;
            var rooms = app.Ctx.Get<ICollaborationRoomService>(ICollaborationRoomService.ServiceName)!;
            var channels = app.Ctx.Get<IExternalChannelRouter>(IExternalChannelRouter.ServiceName)!;
            var approval = app.Ctx.Get<Dsh.Interaction.ApprovalService>(Dsh.Interaction.ApprovalService.ServiceName)!;
            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;

            var targetAgent = await agents.CreateAsync(new AgentProfileRegistration("guarded-agent", Cwd: projectDir), Ct);
            var liveAgent = await agents.WakeAsync(targetAgent.AgentId, Ct);

            // 1. Even under ApprovalPolicy.Auto + session grant, ExternalAgent attribution cannot auto-approve.
            liveAgent.Session.Append(new Dsh.Core.TurnStartPayload(1));
            approval.SetPolicy(liveAgent, Dsh.Interaction.ApprovalPolicy.Auto);
            liveAgent.Session.Append(new Dsh.Interaction.ApprovalGrantedPayload("write_file"));
            using (AgentTurnAttribution.Enter(new MessageAttribution(
                new MessagePrincipal(PrincipalKind.Agent, "ext-1", "Ext"),
                MessageTrustLevel.ExternalAgent,
                AgentMessageIntent.Task)))
            {
                var outcome = await approval.Request(
                    new ApprovalRequest(liveAgent, "write_file", ToolCallId.Create("call-1"), "external write"),
                    Ct);
                Assert.Equal(ApprovalOutcome.Unavailable, outcome);
            }
            liveAgent.Session.Append(new Dsh.Core.TurnEndPayload(1, new Dsh.Core.TurnEndReason.Completed()));

            // 2. External channel bridgeToRoom keeps ExternalAgent trust when prompting @agent.
            var owner = new MessagePrincipal(PrincipalKind.Human, "owner-id", "Owner");
            var room = await rooms.CreateRoomAsync("bridged-room", owner, new RoomPolicy(), Ct);
            await rooms.AddAgentAsync(room.Id, owner, targetAgent.AgentId, "guarded-agent", Ct);
            channels.Bind(new ChannelBinding(
                "gh-bridge",
                "github",
                "org/repo",
                targetAgent.AgentId,
                room.Id,
                Metadata: new Dictionary<string, string> { ["bridgeToRoom"] = "true" }));
            await channels.DispatchInboundAsync(new ChannelInboundMessage(
                "gh-bridge",
                "github",
                "org/repo",
                "gh-msg-1",
                "gh-user",
                "gh-user",
                "@guarded-agent check",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), Ct);
            var bridgedEnvelope = liveAgent.Session.SnapshotEvents()
                .Select(e => e.Data)
                .OfType<AgentEnvelopePayload>()
                .Last(p => p.Envelope.Sender.Kind == PrincipalKind.ExternalChannel);
            Assert.Equal(MessageTrustLevel.ExternalAgent, bridgedEnvelope.Envelope.TrustLevel);

            // 3. RemoteHost role gates: non-member Observer denied room read; Collaborator denied SetApprovalPolicy, Control, and /discover trust.
            var privateRoom = await rooms.CreateRoomAsync("private-room", owner, new RoomPolicy(AllowObserverRead: false), Ct);
            var privateObserverInvite = await rooms.CreateInviteAsync(privateRoom.Id, owner, RoomRole.Observer, cancellationToken: Ct);
            var collabInvite = await rooms.CreateInviteAsync(room.Id, owner, RoomRole.Collaborator, cancellationToken: Ct);

            await using var hostBackend = await HarnessRemoteHostBackend.CreateAsync(app, HostPtyDaemonForTests.Unused, Ct);
            var server = new RemoteHostServer(new RemoteHostServerOptions(Token: "master-secret"), hostBackend);

            var (obsServer, obsClientStream) = FullDuplexStream.CreatePair();
            var obsTask = server.ServeAsync(obsServer, Ct);
            await using var obsClient = await RemoteHostClient.ConnectAsync(obsClientStream, null, "obs", "obs-user", privateObserverInvite.Token, Ct);
            var obsMesh = obsClient.GetCapability<IWorkspaceMesh>()!;
            await Assert.ThrowsAnyAsync<Exception>(() => obsMesh.ReadRoomMessagesAsync(privateRoom.Id.Value, cancellationToken: Ct));

            var (colServer, colClientStream) = FullDuplexStream.CreatePair();
            var colTask = server.ServeAsync(colServer, Ct);
            await using var colClient = await RemoteHostClient.ConnectAsync(colClientStream, null, "col", "col-user", collabInvite.Token, Ct);
            var colMesh = colClient.GetCapability<IWorkspaceMesh>()!;
            await Assert.ThrowsAnyAsync<Exception>(() => colClient.SessionEditing.SetSessionApprovalPolicyAsync(liveAgent.Id.Value, "auto", Ct));
            await Assert.ThrowsAnyAsync<Exception>(() => colClient.SessionEditing.ExecuteSessionCommandAsync(liveAgent.Id.Value, "/discover trust card-1", Ct));
            await Assert.ThrowsAnyAsync<Exception>(() => colMesh.SendToAgentAsync(
                new RemoteMeshSendRequest(targetAgent.AgentId.Value, "hijack", AgentMessageIntent.Control), Ct));

            // 4. ExternalAgent turn cannot invoke agent_trust grant.
            using (AgentTurnAttribution.Enter(new MessageAttribution(
                new MessagePrincipal(PrincipalKind.Agent, "ext-2", "Ext"),
                MessageTrustLevel.ExternalAgent,
                AgentMessageIntent.Task)))
            {
                var trustResult = await tools.Execute(new ToolExecutionInput
                {
                    CallId = ToolCallId.Create("call-trust"),
                    Name = "agent_trust",
                    Arguments = JsonDocument.Parse("""{"action":"grant","card_id":"c1"}""").RootElement,
                    Agent = liveAgent,
                    Signal = Ct,
                });
                Assert.True(trustResult.IsError);
            }

            obsClientStream.Dispose();
            colClientStream.Dispose();
            await Task.WhenAll(obsTask, colTask);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    private static string ComputeHmacHex(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexStringLower(hash);
    }
}
