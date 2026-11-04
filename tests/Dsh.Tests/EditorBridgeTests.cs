using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dsh.Core;
using Dsh.Ide.Bridge;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

public sealed class EditorBridgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 10000)]
    public async Task OptionalDomainWorksWithoutChatAndNeverLaunchesTemplatesReceivedFromTheWire()
    {
        await using var core = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        using var clients = new EditorClientDispatcher();
        var service = new RecordingService();
        var backend = new Backend(core, new EditorBackend(service, clients, core.Files.Policy));
        var server = new RemoteHostServer(new("editor-test-token"), backend);
        await using var connection = await Connection.CreateAsync(server, Ct);
        var editor = connection.Client.Editor;

        Assert.True(connection.Client.Supports(EditorProtocol.Capability));
        Assert.True(connection.Client.Supports(EditorProtocol.ClientCapability));
        Assert.Empty(await connection.Client.ListSessionsAsync(Ct));
        Assert.Single((await editor.ListAsync(Ct)).Descriptors);
        Assert.Equal(core.Root, (await editor.StatusAsync(core.Root, Ct)).Workspace.Root);
        Assert.Single(await editor.DoctorAsync(core.Root, Ct));
        Assert.True((await editor.OpenAsync(new EditorRequest { WorkspaceRoot = core.Root, ExecutionTarget = EditorExecutionTarget.Local }, Ct)).Success);
        Assert.Single(service.Requests);

        using var injected = JsonDocument.Parse("{\"OperationId\":\"" + Guid.NewGuid() + "\",\"Request\":{\"WorkspaceRoot\":" + JsonSerializer.Serialize(core.Root) + ",\"Command\":\"untrusted-command\",\"Environment\":{\"X\":\"untrusted\"}}}");
        await Assert.ThrowsAsync<JsonRpcException>(() => connection.Client.RequestAsync(EditorProtocol.MethodOpen, injected.RootElement, Ct));
        Assert.Single(service.Requests);
        await Assert.ThrowsAsync<JsonRpcException>(() => editor.OpenAsync(new EditorRequest { WorkspaceRoot = Path.GetPathRoot(core.Root)! }, Ct));
        Assert.Single(service.Requests);
    }

    [Fact(Timeout = 10000)]
    public async Task AbsentDomainDoesNotAdvertiseOrPretendToOpen()
    {
        await using var core = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        var server = new RemoteHostServer(new("editor-test-token"), core);
        await using var connection = await Connection.CreateAsync(server, Ct);
        Assert.False(connection.Client.Supports(EditorProtocol.Capability));
        var error = await Assert.ThrowsAsync<JsonRpcException>(() => connection.Client.Editor.ListAsync(Ct));
        Assert.Equal(JsonRpcPeer.MethodNotFound, error.Code);
    }

    [Fact(Timeout = 10000)]
    public async Task UnauthenticatedPeerCannotRegisterOrOpen()
    {
        await using var core = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        using var clients = new EditorClientDispatcher();
        var service = new RecordingService();
        var server = new RemoteHostServer(new("editor-test-token"), new Backend(core, new EditorBackend(service, clients, core.Files.Policy)));
        await using var raw = await RawConnection.CreateAsync(server, Ct);
        var denied = await Assert.ThrowsAsync<JsonRpcException>(() => raw.Peer.RequestAsync(EditorProtocol.MethodRegister,
            JsonSerializer.SerializeToElement(new RemoteEditorClientRegistration(), EditorProtocolJsonContext.Default.RemoteEditorClientRegistration), Ct));
        Assert.Equal(-32001, denied.Code);
        await Assert.ThrowsAsync<JsonRpcException>(() => raw.Peer.RequestAsync(EditorProtocol.MethodOpen,
            JsonSerializer.SerializeToElement(new RemoteEditorOpen(Guid.NewGuid(), new EditorRequest { WorkspaceRoot = core.Root }), EditorProtocolJsonContext.Default.RemoteEditorOpen), Ct));
        Assert.Empty(clients.Clients);
        Assert.Empty(service.Requests);
    }

    [Fact(Timeout = 10000)]
    public async Task ExplicitClientAndManualRequestsReachOnlyTheSelectedClientBeforeHostDetection()
    {
        await using var core = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        using var clients = new EditorClientDispatcher();
        var hostService = new RecordingService();
        var bridge = new EditorBridgeService(hostService, clients);
        var server = new RemoteHostServer(new("editor-test-token"), new Backend(core, new EditorBackend(bridge, clients, core.Files.Policy)));
        await using var first = await Connection.CreateAsync(server, Ct);
        await using var second = await Connection.CreateAsync(server, Ct);
        var desktopOne = new RecordingService();
        var desktopTwo = new RecordingService();
        await using var registrationOne = await first.Client.Editor.RegisterClientAsync(desktopOne, Ct, request => request with { WorkspaceRoot = core.Root });
        await using var registrationTwo = await second.Client.Editor.RegisterClientAsync(desktopTwo, Ct, request => request with { WorkspaceRoot = core.Root });
        var newSession = new EditorRequest { WorkspaceRoot = core.Root, SessionId = "new-session", ExecutionTarget = EditorExecutionTarget.Client };
        Assert.True((await first.Client.Editor.OpenAsync(newSession, Ct)).Success);
        Assert.True((await second.Client.Editor.OpenAsync(newSession, Ct)).Success);
        Assert.Equal(2, desktopOne.Requests.Count);
        Assert.Empty(desktopTwo.Requests);
        await first.Client.Editor.SelectForSessionAsync("session-a", Ct);
        await second.Client.Editor.SelectForSessionAsync("session-b", Ct);

        var request = new EditorRequest { WorkspaceRoot = core.Root, SessionId = "session-a", TriggerId = "session.start", ExecutionTarget = EditorExecutionTarget.Client, MachineId = "remote" };
        Assert.True((await bridge.LaunchAsync(request, Ct)).Success);
        Assert.True((await second.Client.Editor.OpenAsync(request with { TriggerId = null }, Ct)).Success);
        Assert.Equal(4, desktopOne.Requests.Count);
        Assert.Empty(desktopTwo.Requests);
        Assert.Empty(hostService.Requests);
        Assert.Equal(0, hostService.Detections);
        Assert.All(desktopOne.Requests, forwarded =>
        {
            Assert.Equal(EditorExecutionTarget.Local, forwarded.ExecutionTarget);
            Assert.Null(forwarded.MachineId);
            Assert.Null(forwarded.HostEditor);
        });
        var automatic = await bridge.LaunchAsync(request with { ExecutionTarget = EditorExecutionTarget.Automatic }, Ct);
        Assert.True(automatic.Success);
        var automaticRequest = Assert.Single(hostService.Requests);
        Assert.Equal(EditorExecutionTarget.Local, automaticRequest.ExecutionTarget);
        Assert.Equal(0, hostService.Detections);
        Assert.True((await bridge.LaunchAsync(request with { ExecutionTarget = EditorExecutionTarget.Local }, Ct)).Success);
        Assert.Equal(2, hostService.Requests.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task HostStatusExplainsTheLatestClientSelectionAndThenTheLocalSelection()
    {
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        await using var core = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        using var clients = new EditorClientDispatcher();
        var desktop = fixture.Service(fixture.Profile("desktop"));
        var host = fixture.Service(fixture.Profile("host"));
        var bridge = new EditorBridgeService(host, clients);
        var server = new RemoteHostServer(new("editor-test-token"), new Backend(core, new EditorBackend(bridge, clients, core.Files.Policy)));
        await using var connection = await Connection.CreateAsync(server, Ct);
        await using var registration = await connection.Client.Editor.RegisterClientAsync(desktop, Ct,
            request => request with { WorkspaceRoot = fixture.Root, Path = fixture.Root });
        await connection.Client.Editor.SelectForSessionAsync("decision-session", Ct);
        var request = new EditorRequest
        {
            WorkspaceRoot = core.Root, Path = core.Root, SessionId = "decision-session",
            Editor = "desktop", Action = EditorAction.OpenWorkspace,
        };
        var remote = await connection.Client.Editor.OpenAsync(request with { ExecutionTarget = EditorExecutionTarget.Client }, Ct);
        Assert.True(remote.Success, remote.Message);
        var selected = Assert.IsType<EditorDecision>((await connection.Client.Editor.StatusAsync(core.Root, Ct)).LastDecision);
        Assert.Equal(["desktop"], selected.CandidateOrder);
        Assert.Collection(selected.Steps, step =>
        {
            Assert.Equal("desktop", step.Editor.Value);
            Assert.Equal("explicit", step.Source);
            Assert.Equal(EditorErrorCode.None, step.Error);
        });
        var local = await connection.Client.Editor.OpenAsync(request with { Editor = "host", ExecutionTarget = EditorExecutionTarget.Local }, Ct);
        Assert.True(local.Success, local.Message);
        var localSelection = Assert.IsType<EditorDecision>((await connection.Client.Editor.StatusAsync(core.Root, Ct)).LastDecision);
        Assert.Equal(["host"], localSelection.CandidateOrder);
    }

    [Fact(Timeout = 10000)]
    public async Task CancellationRoundTripsAndAcknowledgesClientRetirement()
    {
        await using var core = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        using var clients = new EditorClientDispatcher();
        var bridge = new EditorBridgeService(new RecordingService(), clients);
        var server = new RemoteHostServer(new("editor-test-token"), new Backend(core, new EditorBackend(bridge, clients, core.Files.Policy)));
        await using var connection = await Connection.CreateAsync(server, Ct);
        var started = NewSignal();
        var retired = NewSignal();
        var local = new RecordingService(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return new EditorResult(); }
            finally { retired.TrySetResult(); }
        });
        await using var registration = await connection.Client.Editor.RegisterClientAsync(local, Ct, request => request);
        await connection.Client.Editor.SelectForSessionAsync("cancel-session", Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var open = connection.Client.Editor.OpenAsync(new EditorRequest { WorkspaceRoot = core.Root, SessionId = "cancel-session", ExecutionTarget = EditorExecutionTarget.Client }, cancellation.Token);
        await started.Task.WaitAsync(Ct);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        Assert.True(retired.Task.IsCompletedSuccessfully);
        Assert.True(await connection.Client.PingAsync(Ct) > 0);
    }

    [Fact(Timeout = 10000)]
    public async Task DisconnectRetiresInFlightRequestsAndNeverReassignsAnotherDesktop()
    {
        await using var core = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        using var clients = new EditorClientDispatcher();
        var server = new RemoteHostServer(new("editor-test-token"), new Backend(core, new EditorBackend(new RecordingService(), clients, core.Files.Policy)));
        await using var connection = await Connection.CreateAsync(server, Ct);
        var started = NewSignal();
        var retired = NewSignal();
        var local = new RecordingService(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return new EditorResult(); }
            finally { retired.TrySetResult(); }
        });
        var registration = await connection.Client.Editor.RegisterClientAsync(local, Ct, request => request);
        await connection.Client.Editor.SelectForSessionAsync("disconnect-session", Ct);
        var request = new EditorRequest { WorkspaceRoot = core.Root, SessionId = "disconnect-session", ExecutionTarget = EditorExecutionTarget.Client };
        var open = clients.LaunchAsync(request, Ct).AsTask();
        await started.Task.WaitAsync(Ct);
        await connection.DisposeAsync();
        await retired.Task.WaitAsync(Ct);
        Assert.Equal(EditorErrorCode.CLIENT_UNAVAILABLE, (await open).Error);
        Assert.Empty(clients.Clients);
        Assert.Equal(EditorErrorCode.CLIENT_UNAVAILABLE, (await clients.LaunchAsync(request, Ct)).Error);
        // Registration lifetime can end after its connection without sending another RPC.
        await registration.DisposeAsync();
    }

    [Fact(Timeout = 10000)]
    public async Task ReverseBridgeHonorsTheLocalLaunchersCurrentPermissionBeforeDetection()
    {
        await using var core = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        using var clients = new EditorClientDispatcher();
        var server = new RemoteHostServer(new("editor-test-token"), new Backend(core, new EditorBackend(new RecordingService(), clients, core.Files.Policy)));
        await using var connection = await Connection.CreateAsync(server, Ct);
        var allowed = false;
        var local = new Dsh.Ide.Launch.EditorLauncher(new Dsh.Ide.Catalog.EditorCatalog([]), canLaunch: () => allowed);
        await using var registration = await connection.Client.Editor.RegisterClientAsync(local, Ct, request => request);
        await connection.Client.Editor.SelectForSessionAsync("permission-session", Ct);
        var request = new EditorRequest { WorkspaceRoot = core.Root, SessionId = "permission-session", ExecutionTarget = EditorExecutionTarget.Client };
        Assert.Equal(EditorErrorCode.PERMISSION_DENIED, (await clients.LaunchAsync(request, Ct)).Error);
        allowed = true;
        Assert.Equal(EditorErrorCode.NOT_FOUND, (await clients.LaunchAsync(request, Ct)).Error);
    }

    [Fact(Timeout = 10000)]
    public async Task RegistrationRequiresTrustedMappingAndUnregisterWaitsForActiveLocalWork()
    {
        await using var core = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        using var clients = new EditorClientDispatcher();
        var server = new RemoteHostServer(new("editor-test-token"), new Backend(core, new EditorBackend(new RecordingService(), clients, core.Files.Policy)));
        await using var connection = await Connection.CreateAsync(server, Ct);
        var untouched = new RecordingService();
        var missingMapping = await connection.Client.Editor.RegisterClientAsync(untouched, Ct);
        await connection.Client.Editor.SelectForSessionAsync("unmapped", Ct);
        var request = new EditorRequest { WorkspaceRoot = core.Root, SessionId = "unmapped", ExecutionTarget = EditorExecutionTarget.Client };
        Assert.Equal(EditorErrorCode.UNSUPPORTED, (await clients.LaunchAsync(request, Ct)).Error);
        Assert.Empty(untouched.Requests);
        await missingMapping.DisposeAsync();
        var started = NewSignal();
        var retired = NewSignal();
        var local = new RecordingService(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return new EditorResult(); }
            finally { retired.TrySetResult(); }
        });
        var registration = await connection.Client.Editor.RegisterClientAsync(local, Ct, value => value);
        await connection.Client.Editor.SelectForSessionAsync("unmapped", Ct);
        var open = clients.LaunchAsync(request, Ct).AsTask();
        await started.Task.WaitAsync(Ct);
        await registration.DisposeAsync();
        Assert.True(retired.Task.IsCompletedSuccessfully);
        Assert.Equal(EditorErrorCode.CLIENT_UNAVAILABLE, (await open).Error);
        Assert.Empty(clients.Clients);
    }

    [Fact(Timeout = 10000)]
    public async Task CapturedOriginSurvivesAWorkerCreatedBeforeTheHostRequest()
    {
        using var clients = new EditorClientDispatcher();
        var first = new RecordingService();
        var second = new RecordingService();
        using var firstRegistration = clients.RegisterClient("origin", first.LaunchAsync, TestContext.Current.CancellationToken);
        using var secondRegistration = clients.RegisterClient("other", second.LaunchAsync, TestContext.Current.CancellationToken);
        var queued = NewSignal();
        var request = new EditorRequest { WorkspaceRoot = ".", SessionId = "queued-session", ExecutionTarget = EditorExecutionTarget.Client };
        var worker = Task.Run(async () =>
        {
            await queued.Task.WaitAsync(Ct);
            return await clients.LaunchAsync(request, Ct);
        }, Ct);
        using (clients.EnterClientScope("origin"))
        {
            clients.BindCurrentClient("queued-session");
            queued.TrySetResult();
        }
        Assert.True((await worker).Success);
        Assert.Single(first.Requests);
        Assert.Empty(second.Requests);
        using (clients.EnterClientScope("other"))
            clients.BindCurrentClient("queued-session");
        Assert.True((await clients.LaunchAsync(request, Ct)).Success);
        Assert.Equal(2, first.Requests.Count);
        Assert.Empty(second.Requests);
    }

    [Fact]
    public async Task AutomaticUsesTheHostWithoutSelectingAnArbitraryConnectedClient()
    {
        using var clients = new EditorClientDispatcher();
        var local = new RecordingService();
        var other = new RecordingService();
        using var registration = clients.RegisterClient("unselected", other.LaunchAsync, TestContext.Current.CancellationToken);
        var bridge = new EditorBridgeService(local, clients);
        var result = await bridge.LaunchAsync(new EditorRequest { WorkspaceRoot = ".", SessionId = "ssh", ExecutionTarget = EditorExecutionTarget.Automatic }, Ct);
        Assert.True(result.Success);
        Assert.False(result.Printed);
        Assert.Equal(EditorExecutionTarget.Local, Assert.Single(local.Requests).ExecutionTarget);
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task AutomaticNeverDispatchesToAClient()
    {
        using var clients = new EditorClientDispatcher();
        var client = new RecordingService();
        var local = new RecordingService();
        using var registration = clients.RegisterClient("client", client.LaunchAsync, Ct);
        clients.SelectClient("automatic-session", "client");
        var bridge = new EditorBridgeService(local, clients);
        var result = await bridge.LaunchAsync(new EditorRequest { WorkspaceRoot = ".", SessionId = "automatic-session", ExecutionTarget = EditorExecutionTarget.Automatic }, Ct);
        Assert.True(result.Success);
        Assert.Equal(EditorExecutionTarget.Local, Assert.Single(local.Requests).ExecutionTarget);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task AutomaticUsesTheClientOnlyWhenTheRemoteClientHasMappings()
    {
        using var clients = new EditorClientDispatcher();
        var client = new RecordingService();
        var local = new RecordingService();
        using var registration = clients.RegisterClient("client", client.LaunchAsync, Ct, isLocalMachine: false, hasMappings: true);
        clients.SelectClient("mapped-session", "client");
        var bridge = new EditorBridgeService(local, clients);
        var result = await bridge.LaunchAsync(new EditorRequest { WorkspaceRoot = ".", SessionId = "mapped-session", ExecutionTarget = EditorExecutionTarget.Automatic }, Ct);
        Assert.True(result.Success);
        Assert.Single(client.Requests);
        Assert.Empty(local.Requests);
    }

    [Fact]
    public async Task AutomaticUsesTheHostForALocalClientWithMappings()
    {
        using var clients = new EditorClientDispatcher();
        var client = new RecordingService();
        var local = new RecordingService();
        using var registration = clients.RegisterClient("client", client.LaunchAsync, Ct, isLocalMachine: true, hasMappings: true);
        clients.SelectClient("local-session", "client");
        var bridge = new EditorBridgeService(local, clients);
        var result = await bridge.LaunchAsync(new EditorRequest { WorkspaceRoot = ".", SessionId = "local-session", ExecutionTarget = EditorExecutionTarget.Automatic }, Ct);
        Assert.True(result.Success);
        Assert.Equal(EditorExecutionTarget.Local, Assert.Single(local.Requests).ExecutionTarget);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void ClientMappingAvailabilityRequiresAWorkspaceMatch()
    {
        var mapper = new EditorPathMapper([new("remote", "/workspace", Directory.GetCurrentDirectory())]);
        Assert.True(mapper.HasMapping("remote", "/workspace"));
        Assert.True(mapper.HasMapping("remote", "/workspace/src"));
        Assert.False(mapper.HasMapping("remote", "/other"));
        Assert.False(mapper.HasMapping("other", "/workspace"));
    }

    [Fact]
    public async Task ExplicitClientWithoutMappingDoesNotFallBackToTheHost()
    {
        using var clients = new EditorClientDispatcher();
        var client = new RecordingService((_, _) => ValueTask.FromResult(new EditorResult
        {
            Error = EditorErrorCode.UNSUPPORTED,
            Message = "A locally trusted remote workspace mapping is required.",
        }));
        var local = new RecordingService();
        using var registration = clients.RegisterClient("client", client.LaunchAsync, Ct);
        clients.SelectClient("client-session", "client");
        var bridge = new EditorBridgeService(local, clients);
        var result = await bridge.LaunchAsync(new EditorRequest { WorkspaceRoot = ".", SessionId = "client-session", ExecutionTarget = EditorExecutionTarget.Client }, Ct);
        Assert.Equal(EditorErrorCode.UNSUPPORTED, result.Error);
        Assert.Empty(local.Requests);
    }

    [Fact(Timeout = 10000)]
    public async Task ClientRegistrationCarriesMappingAvailability()
    {
        await using var core = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        using var clients = new EditorClientDispatcher();
        var server = new RemoteHostServer(new("editor-test-token"), new Backend(core, new EditorBackend(new RecordingService(), clients, core.Files.Policy)));
        await using var connection = await Connection.CreateAsync(server, Ct);
        var client = new RecordingService();
        await using var registration = await connection.Client.Editor.RegisterClientAsync(client, Ct, request => request, isLocalMachine: false, hasMappings: true);
        await connection.Client.Editor.SelectForSessionAsync("mapped-session", Ct);
        Assert.Equal(EditorExecutionTarget.Client, clients.TargetFor("mapped-session"));
    }

    [Theory]
    [InlineData(EditorPathMappingKind.Ssh, "build-host", "ssh-remote+build-host")]
    [InlineData(EditorPathMappingKind.Wsl, "Ubuntu", "wsl+Ubuntu")]
    [InlineData(EditorPathMappingKind.DevContainer, "dev-container+616263", "dev-container+616263")]
    public void ExplicitMappingsBuildRemoteUrisAndRefuseAuthorityOverrides(EditorPathMappingKind kind, string authority, string expected)
    {
        var root = TempTree.CreateDirectory("editor-map");
        try
        {
            var mapper = new EditorPathMapper([new("remote", "/workspace", root, kind, authority)]);
            var request = new EditorRequest { WorkspaceRoot = "/workspace", Path = "src/a b.cs", Action = EditorAction.OpenAtPosition, MachineId = "remote" };
            var result = mapper.Map("remote", request);
            Assert.Equal("vscode-remote://" + expected + "/workspace/src/a%20b.cs", result.RemoteUri);
            Assert.Equal(Path.Combine(root, "src", "a b.cs"), result.Path);
            Assert.Null(result.MachineId);
            Assert.Throws<ArgumentException>(() => mapper.Map("remote", request with { Path = "../outside.cs" }));
            Assert.Throws<ArgumentException>(() => mapper.Map("remote", request with { WorkspaceRoot = "/workspace-other" }));
            Assert.Throws<ArgumentException>(() => mapper.Map("remote", request with { RemoteUri = "vscode-remote://ssh-remote+other/etc/passwd" }));
            Assert.Throws<NotSupportedException>(() => mapper.Map("remote", request with { Action = EditorAction.OpenDiff }));
        }
        finally { TempTree.Delete(root); }
    }

    [Theory]
    [InlineData(EditorPathMappingKind.LocalMirror)]
    [InlineData(EditorPathMappingKind.Ssh)]
    public void TrustedConnectionChoosesMappingsInsteadOfTheWireMachineId(EditorPathMappingKind kind)
    {
        var firstRoot = TempTree.CreateDirectory("editor-machine-a");
        var secondRoot = TempTree.CreateDirectory("editor-machine-b");
        try
        {
            var mapper = new EditorPathMapper(
            [
                new("machine-a", "/workspace", firstRoot, kind, "host-a"),
                new("machine-b", "/workspace", secondRoot, kind, "host-b"),
            ]);
            var request = new EditorRequest { WorkspaceRoot = "/workspace", Path = "source.cs", MachineId = "machine-b" };
            var first = mapper.Map("machine-a", request);
            var second = mapper.Map("machine-b", request with { MachineId = "machine-a" });
            Assert.Equal(Path.Combine(firstRoot, "source.cs"), first.Path);
            Assert.Equal(Path.Combine(secondRoot, "source.cs"), second.Path);
            Assert.Equal(kind == EditorPathMappingKind.Ssh ? "vscode-remote://ssh-remote+host-a/workspace/source.cs" : null, first.RemoteUri);
            Assert.Equal(kind == EditorPathMappingKind.Ssh ? "vscode-remote://ssh-remote+host-b/workspace/source.cs" : null, second.RemoteUri);
            Assert.Throws<ArgumentException>(() => mapper.Map("unmapped", request));
        }
        finally { TempTree.Delete(firstRoot); TempTree.Delete(secondRoot); }
    }

    [Fact]
    public void UnscopedMappingsCannotAuthorizeAnyConnectedMachine()
    {
        var root = TempTree.CreateDirectory("editor-machine-map");
        try
        {
            Assert.Throws<ArgumentException>(() => EditorPathMapper.From(new Dictionary<string, object?>
            {
                ["client_mappings"] = new object[]
                {
                    new Dictionary<string, object?> { ["source_root"] = "/workspace", ["destination_root"] = root },
                },
            }));
        }
        finally { TempTree.Delete(root); }
    }

    [Fact]
    public void MirrorMappingsEnforceLocalSymlinkBoundaryAndRejectProcessSettings()
    {
        var root = TempTree.CreateDirectory("editor-map");
        var outside = TempTree.CreateDirectory("editor-outside");
        try
        {
            var mapper = new EditorPathMapper([new("remote", "/work", root)]);
            Assert.Equal(Path.Combine(root, "file.cs"), mapper.Map("remote", new EditorRequest { WorkspaceRoot = "/work", Path = "file.cs" }).Path);
            Assert.Throws<ArgumentException>(() => EditorPathMapper.From(new Dictionary<string, object?>
            {
                ["client_mappings"] = new object[] { new Dictionary<string, object?> { ["machine"] = "remote", ["source_root"] = "/work", ["destination_root"] = root, ["command"] = "not-allowed" } },
            }));
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
                Assert.Throws<ArgumentException>(() => mapper.Map("remote", new EditorRequest { WorkspaceRoot = "/work", Path = "escape/file.cs" }));
            }
        }
        finally { TempTree.Delete(root); TempTree.Delete(outside); }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record EditorBackend(IEditorService Service, EditorClientDispatcher? Clients, HostPathPolicy? Paths = null) : IRemoteEditorBackend;
    private sealed class Backend(IRemoteHostBackend core, IRemoteEditorBackend? editor) : IRemoteHostBackend
    {
        public HostPendingDecisions Pending => core.Pending;
        public HostSessionRegistry Sessions => core.Sessions;
        public IRemoteSessionBackend Session => core.Session;
        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken) => core.InfoAsync(cancellationToken);
        public IRemoteAgentBackend? Agent => null;
        public IRemotePtyBackend? Pty => null;
        public HostFileSystem? Files => null;
        public HostGitService? Git => null;
        public HostNotificationCenter? Notifications => null;
        public IRemoteSessionEditingBackend? SessionEditing => null;
        public IRemotePluginsBackend? Plugins => null;
        public IRemoteNetworkBackend? Network => null;
        public IRemoteEditorBackend? Editor => editor;
    }

    private sealed class RecordingService(Func<EditorRequest, CancellationToken, ValueTask<EditorResult>>? handler = null) : IEditorService, IEditorCatalog
    {
        public ConcurrentQueue<EditorRequest> Requests { get; } = new();
        private int _detections;
        public int Detections => Volatile.Read(ref _detections);
        public IEditorCatalog Catalog => this;
        public EditorDecision? LastDecision => null;
        public IReadOnlyList<EditorDescriptor> Descriptors { get; } = [new EditorDescriptor { Id = "fixture", Command = "fixture-editor", Capabilities = EditorCapability.OpenWorkspace }];
        public EditorDescriptor? Find(EditorId id) => Descriptors.FirstOrDefault(value => value.Id == id);
        public IDisposable Register(EditorDescriptor descriptor) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<EditorInstallation>> DetectAsync(bool refresh = false, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _detections);
            return ValueTask.FromResult<IReadOnlyList<EditorInstallation>>([new EditorInstallation("fixture", "fixture-editor", "test-fixture")]);
        }
        public EditorWorkspace InspectWorkspace(string root) => new(root, []);
        public EditorBinding? GetBinding(string root, string? sessionId = null) => null;
        public void Bind(EditorBinding binding) { }
        public ValueTask<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<EditorDiagnostic>>([new("fixture", true, "Fixture service is available.")]);
        public ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            return handler is null ? ValueTask.FromResult(new EditorResult { Success = true, Printed = request.Print, PrintableCommand = request.Print ? "fixture-editor --open ." : null }) : handler(request, cancellationToken);
        }
    }

    private sealed class Connection(RawConnection transport, RemoteHostClient client) : IAsyncDisposable
    {
        private Task? _dispose;
        public RemoteHostClient Client { get; } = client;
        public static async Task<Connection> CreateAsync(RemoteHostServer server, CancellationToken cancellationToken)
        {
            var transport = await RawConnection.CreateAsync(server, cancellationToken, createPeer: false);
            try { return new Connection(transport, await RemoteHostClient.ConnectAsync(transport.Stream, "editor-test-token", cancellationToken)); }
            catch { await transport.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => new(_dispose ??= DisposeCoreAsync());
        private async Task DisposeCoreAsync() { await Client.DisposeAsync(); await transport.DisposeAsync(); }
    }
    private sealed class RawConnection(TcpClient client, TcpClient serverClient, Task serve, JsonRpcPeer? peer) : IAsyncDisposable
    {
        public JsonRpcPeer Peer => peer ?? throw new InvalidOperationException();
        public Stream Stream => client.GetStream();
        public static async Task<RawConnection> CreateAsync(RemoteHostServer server, CancellationToken cancellationToken, bool createPeer = true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accept = listener.AcceptTcpClientAsync(cancellationToken);
            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, cancellationToken);
            var serverClient = await accept;
            var serve = server.ServeAsync(serverClient.GetStream(), cancellationToken);
            var peer = createPeer ? new JsonRpcPeer(client.GetStream()) : null;
            peer?.Start();
            return new RawConnection(client, serverClient, serve, peer);
        }
        public async ValueTask DisposeAsync()
        {
            if (peer is not null) await peer.DisposeAsync();
            client.Dispose();
            await serve.WaitAsync(Ct);
            serverClient.Dispose();
        }
    }
}
