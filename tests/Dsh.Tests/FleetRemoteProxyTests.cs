using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class FleetRemoteProxyTests
{
    private const string ProbeTarget = "http://1.1.1.1/proxy-check";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task Cli_ChangesOnlyTargetHome_ProbesWithoutSaving_AndRedactsCredentials()
    {
        var targetHome = RealHostProcesses.CreateHome("npht");
        var clientHome = RealHostProcesses.CreateHome("npcl");
        var user = $"u{Guid.NewGuid():N}";
        var password = $"p{Guid.NewGuid():N}";
        // This controlled proxy response tests remote routing and privacy, not public forwarding or model completion.
        await using var targetProxy = new TestForwardProxy(requiredCredential: $"{user}:{password}");
        await using var localProxy = new TestForwardProxy();
        try
        {
            await File.AppendAllTextAsync(Path.Combine(targetHome, "settings.yaml"), "\nnetwork:\n  retained: sibling\n", TestContext.Current.CancellationToken);
            HarnessSettings.SaveOutboundProxy(new HarnessHome(clientHome), new OutboundProxySettings
                { Url = $"http://127.0.0.1:{localProxy.Port}" }, new OutboundProxy());
            var added = await FleetCliProcess.RunAsync(RealHostProcesses.Executable, clientHome,
                "machine", "add", "target", "--local", "--home", targetHome);
            Assert.Equal(0, added.ExitCode);
            var clientBefore = await File.ReadAllTextAsync(Path.Combine(clientHome, "settings.yaml"), Ct);
            var url = $"http://{user}:{password}@127.0.0.1:{targetProxy.Port}";
            var saved = await RunCliAsync(clientHome, $$"""{"url":"{{url}}","probeUrl":"{{ProbeTarget}}"}""", TestContext.Current.CancellationToken, "machine", "proxy", "target", "set", "--json-stdin");
            AssertSafeSuccess(saved, user, password);
            Assert.Equal(url, HarnessSettings.Load(new HarnessHome(targetHome)).Network?.OutboundProxy?.Url);
            AssertSafeSuccess(await RunCliAsync(clientHome, null, TestContext.Current.CancellationToken, "machine", "proxy", "target", "show"), user, password);
            var targetBeforeProbe = await File.ReadAllTextAsync(Path.Combine(targetHome, "settings.yaml"), Ct);
            var probe = await RunCliAsync(clientHome, null, TestContext.Current.CancellationToken, "machine", "proxy", "target", "test", "--probe-url", ProbeTarget);
            AssertSafeSuccess(probe, user, password);
            Assert.Contains("远端", probe.Output);
            Assert.Contains($"GET {ProbeTarget} HTTP/1.1", targetProxy.Requests);
            Assert.Equal(targetBeforeProbe, await File.ReadAllTextAsync(Path.Combine(targetHome, "settings.yaml"), Ct));
            var malformedProbe = $"ftp://{user}:{password} secret@example.invalid/check";
            var malformedInput = JsonSerializer.Serialize(new RemoteOutboundProxySettings(url, ProbeUrl: malformedProbe),
                NetworkProtocolJsonContext.Default.RemoteOutboundProxySettings);
            AssertSafeSuccess(await RunCliAsync(clientHome, malformedInput, TestContext.Current.CancellationToken, "machine", "proxy", "target", "set", "--json-stdin"), user, password);
            var invalidProbe = await RunCliAsync(clientHome, null, TestContext.Current.CancellationToken, "machine", "proxy", "target", "test");
            Assert.NotEqual(0, invalidProbe.ExitCode);
            Assert.DoesNotContain(user, invalidProbe.Output + invalidProbe.Error);
            Assert.DoesNotContain(password, invalidProbe.Output + invalidProbe.Error);
            AssertSafeSuccess(await RunCliAsync(clientHome, null, TestContext.Current.CancellationToken, "machine", "proxy", "target", "clear"), user, password);
            Assert.Null(HarnessSettings.Load(new HarnessHome(targetHome)).Network?.OutboundProxy);
            Assert.Contains("retained: sibling", await File.ReadAllTextAsync(Path.Combine(targetHome, "settings.yaml"), Ct));
            Assert.Equal(clientBefore, await File.ReadAllTextAsync(Path.Combine(clientHome, "settings.yaml"), Ct));
            Assert.Empty(localProxy.Requests);
        }
        finally
        {
            RealHostProcesses.KillAll(targetHome);
            RealHostProcesses.KillAll(clientHome);
            TempTree.Delete(targetHome);
            TempTree.Delete(clientHome);
        }
    }

    [Fact(Explicit = true)]
    public async Task TargetChanges_ReachBothWorkspaces_KeepOtherOwnersLive_AndRejectStaleSaves()
    {
        await using var alpha = await HarnessHostFixture.CreateAsync("npa", TestContext.Current.CancellationToken);
        await using var beta = await HarnessHostFixture.CreateAsync("npb", Ct);
        await using var local = await GuiTestEnvironment.CreateAsync();
        await using var alphaProxy = new TestForwardProxy();
        await using var betaProxy = new TestForwardProxy();
        await using var localProxy = new TestForwardProxy();
        using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var first = await alpha.ConnectAsync("first", Ct);
        var alias = await alpha.ConnectAsync("alias", Ct);
        var other = await beta.ConnectAsync("other", Ct);
        var original = await first.Network.GetOutboundAsync(Ct);
        var betaOriginal = await other.Network.GetOutboundAsync(Ct);
        var saved = await first.Network.SetOutboundAsync(new(original.Revision, Settings(alphaProxy)), Ct);
        var betaSaved = await other.Network.SetOutboundAsync(new(betaOriginal.Revision, Settings(betaProxy)), Ct);
        HarnessSettings.SaveOutboundProxy(local.App.Home, new OutboundProxySettings { Url = Settings(localProxy).Url },
            OutboundProxy.FromContext(local.App.Ctx));
        // These are real registered workspaces without installing a synthetic model provider.
        var workspaceA = await first.CreateSessionAsync(alpha.Root, cancellationToken: Ct);
        var workspaceB = await first.CreateSessionAsync(Directory.CreateDirectory(Path.Combine(alpha.Root, "second")).FullName,
            cancellationToken: Ct);
        AssertFetchSuccess(await FetchAsync(alpha.Environment.App.Ctx, RemoteHostMultiClientTests.Agent(alpha, workspaceA.Id)));
        AssertFetchSuccess(await FetchAsync(alpha.Environment.App.Ctx, RemoteHostMultiClientTests.Agent(alpha, workspaceB.Id)));
        var broken = await first.Network.SetOutboundAsync(new(saved.Revision,
            new RemoteOutboundProxySettings($"http://{unavailable.LocalEndPoint}")), Ct);
        Assert.True((await FetchAsync(alpha.Environment.App.Ctx, RemoteHostMultiClientTests.Agent(alpha, workspaceA.Id))).IsError);
        Assert.True((await FetchAsync(alpha.Environment.App.Ctx, RemoteHostMultiClientTests.Agent(alpha, workspaceB.Id))).IsError);
        AssertFetchSuccess(await FetchAsync(beta.Environment.App.Ctx, beta.Environment.Agent));
        AssertFetchSuccess(await FetchAsync(local.App.Ctx, local.Agent));
        var stale = await Assert.ThrowsAsync<JsonRpcException>(() =>
            alias.Network.SetOutboundAsync(new(original.Revision, Settings(betaProxy)), Ct));
        Assert.Equal(NetworkProtocol.RevisionConflict, stale.Code);
        var seen = await alias.Network.GetOutboundAsync(Ct);
        Assert.Equal((original.OwnerId, alpha.Environment.App.Home.Root, broken.Revision, broken.Settings.Url),
            (seen.OwnerId, seen.HostHome, seen.Revision, seen.Settings.Url));
        Assert.NotEqual(original.OwnerId, betaOriginal.OwnerId);
        Assert.Equal(betaSaved.Revision, (await other.Network.GetOutboundAsync(Ct)).Revision);
        Assert.Equal([$"GET {ProbeTarget} HTTP/1.1", $"GET {ProbeTarget} HTTP/1.1"], alphaProxy.Requests);
        Assert.Equal([$"GET {ProbeTarget} HTTP/1.1"], betaProxy.Requests);
        Assert.Equal([$"GET {ProbeTarget} HTTP/1.1"], localProxy.Requests);
        var live = await first.ListSessionsAsync(Ct);
        Assert.Contains(live, session => session.Id == workspaceA.Id && session.Status != HostSessionStatuses.Exited);
        Assert.Contains(live, session => session.Id == workspaceB.Id && session.Status != HostSessionStatuses.Exited);
    }

    [Fact(Explicit = true)]
    public async Task DraftProbe_DoesNotPersistOrChangeTheSharedRoute()
    {
        await using var host = await HarnessHostFixture.CreateAsync("npp", TestContext.Current.CancellationToken);
        await using var savedProxy = new TestForwardProxy();
        await using var draftProxy = new TestForwardProxy();
        var client = await host.ConnectAsync("probe", Ct);
        var before = await client.Network.GetOutboundAsync(Ct);
        var saved = await client.Network.SetOutboundAsync(new(before.Revision, Settings(savedProxy)), Ct);
        var path = Path.Combine(host.Environment.App.Home.Root, "settings.yaml");
        var document = await File.ReadAllTextAsync(path, Ct);
        var probed = await client.Network.ProbeOutboundAsync(new(Settings(draftProxy), ProbeTarget), Ct);
        Assert.True(probed.Reachable, probed.Message);
        Assert.Equal(document, await File.ReadAllTextAsync(path, Ct));
        Assert.Equal(saved.Revision, (await client.Network.GetOutboundAsync(Ct)).Revision);
        AssertFetchSuccess(await FetchAsync(host.Environment.App.Ctx, host.Environment.Agent));
        Assert.Equal([$"GET {ProbeTarget} HTTP/1.1"], draftProxy.Requests);
        Assert.Equal([$"GET {ProbeTarget} HTTP/1.1"], savedProxy.Requests);
    }

    [Fact(Explicit = true)]
    public async Task Revision_IgnoresUnrelatedYamlEdits_ButStillPersistsOnlyTheOwnedChild()
    {
        await using var host = await HarnessHostFixture.CreateAsync("npr", TestContext.Current.CancellationToken);
        await using var proxy = new TestForwardProxy();
        var client = await host.ConnectAsync("revision", Ct);
        var before = await client.Network.GetOutboundAsync(Ct);
        var path = Path.Combine(host.Environment.App.Home.Root, "settings.yaml");
        await File.AppendAllTextAsync(path, "\n# keep this edit\nnetwork:\n  retained: sibling\n", Ct);
        Assert.Equal(before.Revision, (await client.Network.GetOutboundAsync(Ct)).Revision);
        var saved = await client.Network.SetOutboundAsync(new(before.Revision, Settings(proxy)), Ct);
        Assert.NotEqual(before.Revision, saved.Revision);
        var cleared = await client.Network.SetOutboundAsync(new(saved.Revision, new()), Ct);
        Assert.Equal(before.Revision, cleared.Revision);
        var document = await File.ReadAllTextAsync(path, Ct);
        Assert.Contains("# keep this edit", document);
        Assert.Contains("retained: sibling", document);
        Assert.Null(HarnessSettings.Load(host.Environment.App.Home).Network?.OutboundProxy);
    }

    [Fact(Explicit = true)]
    public async Task NullSavedBypass_CanBeReadProbedAndClearedRemotely()
    {
        await using var host = await HarnessHostFixture.CreateAsync("npnu", TestContext.Current.CancellationToken);
        var home = host.Environment.App.Home;
        await File.AppendAllTextAsync(home.SettingsFile, "\nnetwork:\n  outbound_proxy:\n    bypass: null\n", Ct);
        var client = await host.ConnectAsync("null-bypass", Ct);
        var state = await client.Network.GetOutboundAsync(Ct);
        Assert.Empty(state.Settings.Bypass!);
        var probe = await client.Network.ProbeOutboundAsync(new(ProbeUrl: "invalid"), Ct);
        Assert.False(probe.Reachable);
        await client.Network.SetOutboundAsync(new(state.Revision, new()), Ct);
        Assert.Null(HarnessSettings.Load(home).Network?.OutboundProxy);
    }

    [Fact(Explicit = true)]
    public async Task StartupDisposal_SettlesAllNetworkOperationsWithoutCallerCancellation()
    {
        var root = RealHostProcesses.CreateHome("npst");
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task[] pending;
            // This controlled startup gate tests production connection disposal without launching a substitute Host.
            await using (var connection = new MachineConnection(MachineSpec.Local(root), new FleetOptions
            {
                AutoReconnect = false,
                Deploy = new DeploySettings { AutoDeploy = false },
                StartLocalHost = async (_, cancellation) =>
                {
                    starting.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellation);
                },
            }))
            {
                connection.Start();
                await starting.Task.WaitAsync(TestContext.Current.CancellationToken);
                var network = new FleetNetwork(connection);
                pending = [network.GetOutboundAsync(Ct), network.SetOutboundAsync(new("", new()), Ct), network.ProbeOutboundAsync(new(), Ct)];
            }
            foreach (var request in pending)
                await Assert.ThrowsAsync<IOException>(() => request.WaitAsync(TimeSpan.FromSeconds(2), Ct));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task FailedConnection_ReadFailsWithoutWaitingForAnotherConnection()
    {
        var root = RealHostProcesses.CreateHome("npof");
        try
        {
            await using var connection = new MachineConnection(MachineSpec.Local(root),
                new FleetOptions { AutoReconnect = false, Deploy = new DeploySettings { AutoDeploy = false } });
            connection.Start();
            await connection.WaitSettledAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(() => new FleetNetwork(connection).GetOutboundAsync(Ct));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task RealSsh_DefaultProxyConfigurationStillWorks_WhenTargetHttpEgressIsBroken()
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var server = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var home = RealHostProcesses.CreateHome("npss");
        try
        {
            var machine = await ConfigureDefaultSshProxyAsync(server, home, executable);
            var transport = new SshTransport(machine);
            Assert.Equal(0, (await transport.CaptureAsync("true", Ct)).ExitCode);
            Assert.Equal("used\n", await File.ReadAllTextAsync(Path.Combine(server.Root, "proxy-used"), Ct));
            await using var connection = new MachineConnection(machine,
                new FleetOptions { AutoReconnect = false, Deploy = new DeploySettings { AutoDeploy = false } });
            connection.Start();
            await connection.WaitSettledAsync(Ct);
            var network = new FleetNetwork(connection);
            var before = await network.GetOutboundAsync(Ct);
            using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var broken = await network.SetOutboundAsync(new(before.Revision,
                new RemoteOutboundProxySettings($"http://{unavailable.LocalEndPoint}", ProbeUrl: ProbeTarget)), Ct);
            Assert.Equal(0, (await transport.CaptureAsync("true", Ct)).ExitCode);
            var http = await network.ProbeOutboundAsync(new(ProbeUrl: ProbeTarget), Ct);
            Assert.False(http.Reachable);
            Assert.Contains("远端", http.Message);
            var after = await network.GetOutboundAsync(Ct);
            Assert.Equal((before.OwnerId, home, broken.Revision), (after.OwnerId, after.HostHome, after.Revision));
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            TempTree.Delete(home);
            guard.AssertUnchanged();
        }
    }

    private static async Task<MachineSpec> ConfigureDefaultSshProxyAsync(TestSshServer server, string home, string executable)
    {
        var direct = server.Machine("egress", home) with { RemotePath = Path.GetDirectoryName(executable)! };
        var script = Path.Combine(server.Root, "default-proxy.sh");
        var options = string.Join(' ', direct.SshOptions!.Select(option => $"-o {RemoteShell.Quote(option)}"));
        await File.WriteAllTextAsync(script, $"""
            #!/bin/sh
            printf 'used\n' >> {RemoteShell.Quote(Path.Combine(server.Root, "proxy-used"))}
            exec ssh -T -p {direct.Port} -i {RemoteShell.Quote(direct.KeyPath!)} {options} -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o IdentitiesOnly=yes -W "$1:$2" {RemoteShell.Quote($"{direct.User}@{direct.Host}")}

            """, Ct);
        return direct with { Proxy = null, SshOptions = [$"ProxyCommand=sh {RemoteShell.Quote(script)} %h %p", .. direct.SshOptions!] };
    }

    private static RemoteOutboundProxySettings Settings(TestForwardProxy proxy)
        => new($"http://127.0.0.1:{proxy.Port}", ProbeUrl: ProbeTarget);

    private static Task<ToolExecutionResult> FetchAsync(Context context, IAgent agent)
        => context.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = "web_fetch",
            Agent = agent,
            Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["url"] = ProbeTarget }),
            Signal = Ct,
        });

    private static void AssertFetchSuccess(ToolExecutionResult result)
    {
        var text = string.Join("\n", result.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.False(result.IsError, text);
        Assert.Contains(TestForwardProxy.ProxiedBody, text);
    }

    private static void AssertSafeSuccess(FleetCliProcess.Result result, string user, string password)
    {
        Assert.True(result.ExitCode == 0, $"CLI exited {result.ExitCode}: {OutboundProxy.Redact(result.Error)}");
        Assert.DoesNotContain(user, result.Output);
        Assert.DoesNotContain(password, result.Output);
        Assert.DoesNotContain(user, result.Error);
        Assert.DoesNotContain(password, result.Error);
    }

    internal static async Task<FleetCliProcess.Result> RunCliAsync(string home, string? input, CancellationToken cancellationToken, params string[] arguments)
    {
        var info = new ProcessStartInfo(RealHostProcesses.Executable, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = home,
        };
        info.Environment[HarnessHome.HomeEnv] = home;
        using var process = TestProcessScope.Start(info) ?? throw new InvalidOperationException("failed to start dsharp");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        if (input is not null)
            await process.StandardInput.WriteLineAsync(input.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);
        return new FleetCliProcess.Result(process.ExitCode, await output, await error);
    }
}

/** Real process-isolated HTTP forwarding; no user settings, mock providers, or fixed proxy responses. */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class FleetRemoteProxyProcessSmokeTests
{
    private const string PublicProbe = "https://example.com/";
    private const string RealAnswer = "NET77_REAL_OK";

    [Fact(Explicit = true)]
    [Trait("Category", "LlmSmoke")]
    public async Task CliSavedTargetRoute_CarriesRealRemoteWebAndModel_WithoutChangingOtherHosts()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var localHome = RealHostProcesses.CreateHome("n7lc");
        var targetHome = RealHostProcesses.CreateHome("n7ta");
        var otherHome = RealHostProcesses.CreateHome("n7ot");
        try
        {
            var selected = ConfigureSelectedModel(new HarnessHome(targetHome));
            await using var localProxy = new TestForwardProxy(forward: true);
            await using var targetProxy = new TestForwardProxy(forward: true);
            await using var otherProxy = new TestForwardProxy(forward: true);
            HarnessSettings.SaveOutboundProxy(new HarnessHome(localHome), new OutboundProxySettings
                { Url = $"http://127.0.0.1:{localProxy.Port}" }, new OutboundProxy());
            HarnessSettings.SaveOutboundProxy(new HarnessHome(otherHome), new OutboundProxySettings
                { Url = $"http://127.0.0.1:{otherProxy.Port}" }, new OutboundProxy());
            new MachineStore(new HarnessHome(localHome)).Upsert(MachineSpec.Local(targetHome, "target"));
            var localBefore = await File.ReadAllTextAsync(Path.Combine(localHome, "settings.yaml"), cancellation);
            var otherBefore = await File.ReadAllTextAsync(Path.Combine(otherHome, "settings.yaml"), cancellation);
            using var local = StartWithSystemDefault(localHome, null);
            using var target = StartWithSystemDefault(targetHome, null);
            using var other = StartWithSystemDefault(otherHome, null);
            await using var targetClient = await ConnectAsync(targetHome, cancellation);
            var before = await targetClient.Network.GetOutboundAsync(cancellation);
            var input = JsonSerializer.Serialize(new RemoteOutboundProxySettings($"http://127.0.0.1:{targetProxy.Port}"),
                NetworkProtocolJsonContext.Default.RemoteOutboundProxySettings);
            var set = await FleetRemoteProxyTests.RunCliAsync(localHome, input, TestContext.Current.CancellationToken, "machine", "proxy", "target", "set", "--json-stdin");
            Assert.True(set.ExitCode == 0, OutboundProxy.Redact(set.Error));
            var fromCli = JsonSerializer.Deserialize(set.Output, NetworkProtocolJsonContext.Default.RemoteOutboundProxyState)!;
            var current = await targetClient.Network.GetOutboundAsync(cancellation);
            Assert.Equal((before.OwnerId, targetHome, fromCli.Revision, fromCli.Settings.Url),
                (current.OwnerId, current.HostHome, current.Revision, current.Settings.Url));
            Assert.Equal(current.OwnerId, fromCli.OwnerId);
            var session = await targetClient.CreateSessionAsync(targetHome, cancellationToken: cancellation);
            var completed = ReadCompletedTurnAsync(targetClient, session.Id, cancellation);
            await targetClient.SendMessageAsync(session.Id,
                $"Use web_fetch to read {PublicProbe}.\nAfter its successful result, reply with exactly {RealAnswer} and no other text.\nDo not use other tools.",
                [], cancellation);
            var events = await completed;
            var reason = Assert.IsType<TurnEndPayload>(events[^1].Data).Reason;
            var failure = reason is TurnEndReason.Error error
                ? $"{error.Failure.Code} ({error.Failure.Status}): {OutboundProxy.Redact(error.Failure.Message).Replace(selected.Credential, "***", StringComparison.Ordinal)}"
                : reason.Kind;
            Assert.True(reason is TurnEndReason.Completed, failure);
            Assert.Contains(events.Select(item => item.Data).OfType<ToolCallPayload>(), call => call.Name == "web_fetch");
            Assert.Contains(events.Select(item => item.Data).OfType<ToolResultPayload>(),
                result => result.Error is null && result.Message.Block.Content.OfType<TextBlock>().Any(block => block.Text.Contains("Example Domain", StringComparison.Ordinal)));
            var answer = events.Select(item => item.Data).OfType<AssistantMessagePayload>().Last();
            Assert.Equal(RealAnswer, string.Concat(answer.Message.Content.OfType<TextBlock>().Select(block => block.Text)).Trim());
            Assert.Contains(targetProxy.Requests, line => line.StartsWith($"CONNECT {selected.ProviderHost}:443 ", StringComparison.Ordinal));
            Assert.Contains(targetProxy.Requests, line => line.StartsWith("CONNECT ", StringComparison.Ordinal)
                && Uri.TryCreate($"http://{line.Split(' ')[1]}", UriKind.Absolute, out var destination)
                && IPAddress.TryParse(destination.Host, out _));
            await using var localClient = await ConnectAsync(localHome, cancellation);
            await using var otherClient = await ConnectAsync(otherHome, cancellation);
            Assert.True((await localClient.Network.ProbeOutboundAsync(new(ProbeUrl: PublicProbe), cancellation)).Reachable);
            Assert.True((await otherClient.Network.ProbeOutboundAsync(new(ProbeUrl: PublicProbe), cancellation)).Reachable);
            Assert.Equal(["CONNECT example.com:443 HTTP/1.1"], localProxy.Requests);
            Assert.Equal(["CONNECT example.com:443 HTTP/1.1"], otherProxy.Requests);
            Assert.Equal(localBefore, await File.ReadAllTextAsync(Path.Combine(localHome, "settings.yaml"), cancellation));
            Assert.Equal(otherBefore, await File.ReadAllTextAsync(Path.Combine(otherHome, "settings.yaml"), cancellation));
        }
        finally
        {
            foreach (var home in new[] { localHome, targetHome, otherHome })
            {
                RealHostProcesses.KillAll(home);
                TempTree.Delete(home);
            }
        }
    }

    private static (string ProviderHost, string Credential) ConfigureSelectedModel(HarnessHome target)
    {
        var sourceHome = HarnessHome.Resolve();
        var source = HarnessSettings.Load(sourceHome);
        var route = source.ResolveDefaultModel() ?? throw new InvalidOperationException("real smoke requires a selected HTTP provider and model");
        var provider = source.Providers[route.Provider];
        var model = provider.Models[route.Model];
        Assert.NotNull(provider.Options);
        var key = provider.Options.ApiKey ?? (provider.Options.ApiKeyEnv is { } reference ? new EnvCredentials(sourceHome).Get(reference) : null);
        Assert.False(string.IsNullOrWhiteSpace(key), "real smoke requires a usable credential for the selected HTTP provider");
        var settings = HarnessSettings.Load(target);
        settings.GlobalDefaultModel = $"{route.Provider}/{route.Model}";
        settings.Providers = new()
        {
            [route.Provider] = new ProviderSettings
            {
                Type = provider.Type,
                Options = new ProviderOptions { BaseUrl = provider.Options.BaseUrl, ApiKey = key },
                Models = new() { [route.Model] = model },
            },
        };
        settings.Save(target);
        return (new Uri(provider.Options.BaseUrl!).Host, key!);
    }

    private static async Task<List<SessionEvent>> ReadCompletedTurnAsync(RemoteHostClient client, string sessionId, CancellationToken cancellation)
    {
        var events = new List<SessionEvent>();
        await foreach (var item in client.SubscribeAsync(sessionId, 0, cancellation))
        {
            if (item.Event is not { } sessionEvent)
                continue;
            events.Add(sessionEvent);
            if (sessionEvent.Data is TurnEndPayload)
                return events;
        }
        throw new IOException("remote event stream ended before the model turn completed");
    }


    [Fact(Explicit = true)]
    public async Task EmptySavedUrl_UsesEachHostProcessDefault_AndAnUnproxiedProcessConnectsDirectly()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var localHome = RealHostProcesses.CreateHome("npsl");
        var remoteHome = RealHostProcesses.CreateHome("npsr");
        var directHome = RealHostProcesses.CreateHome("npsd");
        await using var localProxy = new TestForwardProxy(forward: true);
        await using var remoteProxy = new TestForwardProxy(forward: true);
        try
        {
        using var local = StartWithSystemDefault(localHome, $"http://127.0.0.1:{localProxy.Port}");
        using var remote = StartWithSystemDefault(remoteHome, $"http://127.0.0.1:{remoteProxy.Port}");
        using var direct = StartWithSystemDefault(directHome, null);
            await using var localClient = await ConnectAsync(localHome, cancellation);
            await using var remoteClient = await ConnectAsync(remoteHome, cancellation);
            await using var directClient = await ConnectAsync(directHome, cancellation);
            var localState = await ClearAsync(localClient, cancellation);
            var remoteState = await ClearAsync(remoteClient, cancellation);
            await ClearAsync(directClient, cancellation);
            Assert.NotEqual(localState.OwnerId, remoteState.OwnerId);
            Assert.True((await localClient.Network.ProbeOutboundAsync(new(ProbeUrl: PublicProbe), cancellation)).Reachable);
            Assert.True((await remoteClient.Network.ProbeOutboundAsync(new(ProbeUrl: PublicProbe), cancellation)).Reachable);
            var unproxied = await directClient.Network.ProbeOutboundAsync(new(ProbeUrl: PublicProbe), cancellation);
            Assert.True(unproxied.Reachable, unproxied.Message);
            Assert.Contains("直连", unproxied.Message);
            Assert.Equal(["CONNECT example.com:443 HTTP/1.1"], localProxy.Requests);
            Assert.Equal(["CONNECT example.com:443 HTTP/1.1"], remoteProxy.Requests);
        }
        finally
        {
            foreach (var home in new[] { localHome, remoteHome, directHome })
            {
                RealHostProcesses.KillAll(home);
                TempTree.Delete(home);
            }
        }
    }

    private static Process StartWithSystemDefault(string home, string? proxy)
    {
        var info = new ProcessStartInfo(RealHostProcesses.Executable, ["host", "serve"])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = home,
        };
        info.Environment[HarnessHome.HomeEnv] = home;
        // These isolate the framework's existing system-default inputs, not a new harness configuration mechanism.
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" })
            info.Environment.Remove(name);
        if (proxy is not null)
        {
            info.Environment["HTTP_PROXY"] = proxy;
            info.Environment["HTTPS_PROXY"] = proxy;
        }
        var process = TestProcessScope.Start(info) ?? throw new InvalidOperationException("failed to start isolated Host");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<RemoteHostClient> ConnectAsync(string home, CancellationToken cancellation)
    {
        var stream = await RemoteHostConnector.ConnectAsync(home, cancellation);
        return await RemoteHostClient.ConnectAsync(stream, null, "default-proxy-smoke", cancellation);
    }

    private static async Task<RemoteOutboundProxyState> ClearAsync(RemoteHostClient client, CancellationToken cancellation)
    {
        var before = await client.Network.GetOutboundAsync(cancellation);
        var cleared = await client.Network.SetOutboundAsync(new(before.Revision, new()), cancellation);
        Assert.Null(cleared.Settings.Url);
        return cleared;
    }
}


