using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Composition;
using Dsh.Runtime.Events;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
[Trait("Category", "SandboxNative")]
public sealed class PluginSandboxIntegrationTests
{
    private const string Alpha = "test/isolated-alpha";
    private const string Beta = "test/isolated-beta";

    [Fact]
    public async Task InspectionAndActivation_NeverConstructUntrustedCodeInMain_AndDirectAccessIsDenied()
    {
        await SandboxTestBackend.RequireAsync();
        using var fixture = new Fixture();
        var inspection = await ConfigBoot.InspectPluginsAsync(fixture.Options);
        Assert.Contains(inspection.Descriptors, descriptor => descriptor.Package == Alpha);
        Assert.Contains(inspection.Descriptors, descriptor => descriptor.Package == Beta);
        fixture.AssertNoMainExecution();

        await fixture.StartAsync();
        await fixture.Identity("alpha");
        await fixture.Identity("beta");
        fixture.AssertNoMainExecution();

        var protectedFile = Path.Combine(fixture.Root, "main-owned.txt");
        await File.WriteAllTextAsync(protectedFile, "Main can read and write this file", TestContext.Current.CancellationToken);
        var directRead = await fixture.Success("alpha", "direct-read", new { path = protectedFile });
        Assert.False(directRead.GetProperty("succeeded").GetBoolean());
        foreach (var operation in new[] { "direct-write", "native-write" })
        {
            await fixture.Success("alpha", operation, new { path = protectedFile });
            Assert.Equal("Main can read and write this file", await File.ReadAllTextAsync(protectedFile, TestContext.Current.CancellationToken));
        }
        var brokerRead = await fixture.Success("alpha", "read", new { path = protectedFile });
        Assert.Equal("Main can read and write this file", Decode(brokerRead, "data"));

        await using var server = await PluginSdkHttpServer.StartAsync();
        var directHttp = await fixture.Success("alpha", "direct-http", new { url = server.Url });
        Assert.False(directHttp.GetProperty("succeeded").GetBoolean());
        Assert.Equal(0, server.Requests);
        var brokerHttp = await fixture.Success("alpha", "http", new
        {
            url = server.Url, method = "POST", body = Convert.ToBase64String(Encoding.UTF8.GetBytes("Main broker can reach this origin")),
        });
        Assert.Equal(201, brokerHttp.GetProperty("status").GetInt32());
        Assert.Equal(1, server.Requests);
        fixture.AssertNoMainExecution();
    }

    [Fact]
    public async Task SandboxActivation_AfterLegacyMainExecution_NeverRunsCurrentCodeInMain()
    {
        using (var legacy = new Fixture(settings => settings.Sandbox = new SandboxSettings { Enabled = false }))
        {
            await legacy.StartAsync();
            await legacy.Identity("alpha");
        }
        using var isolated = new Fixture();
        await isolated.StartAsync();
        await isolated.Identity("alpha");
        isolated.AssertNoMainExecution();
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokerPermissions_ControlRealFileAndLoopbackEffects(bool sandbox)
    {
        using var fixture = new Fixture(settings =>
        {
            settings.Sandbox = new SandboxSettings { Enabled = sandbox };
            settings.Plugins[Beta] = settings.Plugins[Beta] with
            {
                Permissions = new PluginPermissions { Read = false, Write = false, Network = false },
            };
        });
        await fixture.StartAsync();
        await fixture.Identity("alpha");
        await fixture.Identity("beta");
        var source = Path.Combine(fixture.Root, "source.txt");
        var destination = Path.Combine(fixture.Root, "destination.txt");
        await File.WriteAllTextAsync(source, "broker source", TestContext.Current.CancellationToken);
        Assert.Equal("broker source", Decode(await fixture.Success("alpha", "read", new { path = source }), "data"));
        Assert.True((await fixture.Execute("beta", "read", new { path = source })).IsError);

        await fixture.Success("alpha", "write", WriteArguments(destination, "broker output"));
        Assert.Equal("broker output", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        Assert.True((await fixture.Execute("beta", "write", WriteArguments(destination, "denied overwrite"))).IsError);
        Assert.Equal("broker output", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));

        await using var server = await PluginSdkHttpServer.StartAsync();
        var request = new
        {
            url = server.Url, method = "POST",
            headers = new Dictionary<string, string> { ["X-Plugin"] = "sandbox-broker" },
            body = Convert.ToBase64String(Encoding.UTF8.GetBytes("broker request")),
        };
        var response = await fixture.Success("alpha", "http", request);
        Assert.Equal(201, response.GetProperty("status").GetInt32());
        Assert.Equal(server.ResponseBody, Convert.FromBase64String(response.GetProperty("body").GetString()!));
        Assert.Equal("broker request", Encoding.UTF8.GetString(server.RequestBody!));
        Assert.Equal("sandbox-broker", server.RequestHeader);
        Assert.Equal(1, server.Requests);
        Assert.True((await fixture.Execute("beta", "http", request)).IsError);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task DifferentConfigurationsAndPermissions_ShareOneWorker_IncludingAddObservation()
    {
        using var fixture = new Fixture(settings =>
        {
            settings.Plugins[Alpha] = settings.Plugins[Alpha] with
            {
                Parameters = new Dictionary<string, object?> { ["label"] = "alpha configuration" },
            };
            settings.Plugins[Beta] = settings.Plugins[Beta] with
            {
                Parameters = new Dictionary<string, object?> { ["label"] = "beta configuration" },
                Permissions = new PluginPermissions { Write = false },
            };
        });
        await fixture.StartAsync();
        var first = await fixture.Instance("alpha");
        Assert.Equal(first, await fixture.Instance("beta"));
        Assert.Equal("alpha configuration", (await fixture.Success("alpha", "configuration")).GetProperty("label").GetString());
        Assert.Equal("beta configuration", (await fixture.Success("beta", "configuration")).GetProperty("label").GetString());

        var observation = Path.Combine(fixture.Root, "add-observation");
        Directory.CreateDirectory(observation);
        foreach (var suffix in new[] { ".dll", ".deps.json" })
        {
            var name = "Dsh.IsolatedPluginSample" + suffix;
            File.Copy(Path.Combine(Path.GetDirectoryName(fixture.Artifact)!, name), Path.Combine(observation, name));
        }
        using var answerer = ApprovalAnswerers.AutoApprove(fixture.App.Ctx);
        var added = await fixture.ExecuteTool("plugin_manager", new { action = "add", target = Path.Combine(observation, "Dsh.IsolatedPluginSample.dll") }, TestContext.Current.CancellationToken);

        Assert.False(added.IsError);
        Assert.Equal(first, await fixture.Instance("alpha"));
        Assert.Equal(first, await fixture.Instance("beta"));
        fixture.AssertNoMainExecution();
    }

    [Fact]
    public async Task RejectedRegistrationAndLogFrame_ReleasePendingRequests_WithoutRetiringWorker()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var instance = await fixture.Instance("alpha");
        for (var index = 0; index < 40; index++)
            Assert.True((await fixture.Success("alpha", "invalid-registration")).GetProperty("rejected").GetBoolean());
        Assert.True((await fixture.Success("alpha", "oversized-log")).GetProperty("rejected").GetBoolean());
        Assert.True((await fixture.Success("alpha", "log-burst")).GetProperty("completed").GetBoolean());
        Assert.True((await fixture.Success("alpha", "stderr-burst")).GetProperty("completed").GetBoolean());
        Assert.Equal(instance, await fixture.Instance("alpha"));
        Assert.Equal(instance, await fixture.Instance("beta"));
    }

    [Fact]
    public async Task Cancellation_RetiresSharedWorker_AndExplicitEnableRestartsWithoutReplayingCall()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var instance = await fixture.Instance("alpha");
        var marker = Path.Combine(fixture.Root, "before-cancellation.txt");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = fixture.ExecuteTool("isolated_alpha", new
        {
            operation = "wait-after-write", arguments = WriteArguments(marker, "once", append: true),
        }, cancellation.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!File.Exists(marker))
            await Task.Delay(10, deadline.Token);
        cancellation.Cancel();
        Assert.True((await pending).IsError);
        await fixture.WaitForToolsToDisappear();

        await fixture.Manager.EnableAsync(Alpha);

        var restarted = await fixture.Instance("alpha");
        Assert.NotEqual(instance, restarted);
        Assert.Equal(restarted, await fixture.Instance("beta"));
        Assert.Equal("once", await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PermissionChange_RetiresWholeSharedDomain_AndRebuildsSiblingWithItsOwnAuthority()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var previous = await fixture.Instance("alpha");
        Assert.Equal(previous, await fixture.Instance("beta"));

        await fixture.Manager.SetPermissionAsync(Alpha, PluginPermission.Write, false, TestContext.Current.CancellationToken);

        var changed = await fixture.Instance("alpha");
        var sibling = await fixture.Instance("beta");
        Assert.NotEqual(previous, changed);
        Assert.NotEqual(previous, sibling);
        Assert.Equal(changed, sibling);
        Assert.False(HarnessSettings.Load(fixture.Home).Plugins[Alpha].Permissions!.Write);
        var target = Path.Combine(fixture.Root, "after-permission-change.txt");
        Assert.True((await fixture.Execute("alpha", "write", WriteArguments(target, "denied"))).IsError);
        Assert.False(File.Exists(target));
        await fixture.Success("beta", "write", WriteArguments(target, "sibling remains writable"));
        Assert.Equal("sibling remains writable", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisableOrRemove_RetiresWholeSharedDomain_AndExplicitlyRebuildsEnabledSibling(bool remove)
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var previous = await fixture.Instance("alpha");
        Assert.Equal(previous, await fixture.Instance("beta"));
        if (remove)
            await fixture.Manager.RemoveAsync(Alpha);
        else
            await fixture.Manager.DisableAsync(Alpha);

        Assert.Null(fixture.Tools.Get("isolated_alpha"));
        Assert.Null(fixture.App.Composition!.Find(Alpha));
        Assert.False(HarnessSettings.Load(fixture.Home).Plugins[Alpha].Enabled);
        var sibling = await fixture.Instance("beta");
        Assert.NotEqual(previous, sibling);
        Assert.True(File.Exists(fixture.Artifact));
        var target = Path.Combine(fixture.Root, "surviving-sibling.txt");
        await fixture.Success("beta", "write", WriteArguments(target, "sibling rebuilt"));
        Assert.Equal("sibling rebuilt", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        if (!remove)
        {
            await fixture.Manager.EnableAsync(Alpha);
            Assert.Equal(sibling, await fixture.Instance("alpha"));
        }
    }

    [Fact]
    public async Task FaultThenExplicitEnable_RebuildsSiblings_WithoutReplayingCommittedSideEffect()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var previous = await fixture.Instance("alpha");
        var sideEffect = Path.Combine(fixture.Root, "before-crash.txt");
        var failed = await fixture.Execute("alpha", "crash-after-write", WriteArguments(sideEffect, "exactly once\n", append: true));
        Assert.True(failed.IsError);
        Assert.Equal("exactly once\n", await File.ReadAllTextAsync(sideEffect, TestContext.Current.CancellationToken));
        await fixture.WaitForToolsToDisappear();

        await fixture.Manager.EnableAsync(Alpha);

        var recovered = await fixture.Instance("alpha");
        Assert.NotEqual(previous, recovered);
        Assert.Equal(recovered, await fixture.Instance("beta"));
        Assert.Equal("exactly once\n", await File.ReadAllTextAsync(sideEffect, TestContext.Current.CancellationToken));
        var newEffect = Path.Combine(fixture.Root, "after-recovery.txt");
        await fixture.Success("beta", "write", WriteArguments(newEffect, "new explicit invocation"));
        Assert.Equal("new explicit invocation", await File.ReadAllTextAsync(newEffect, TestContext.Current.CancellationToken));
        Assert.Equal("exactly once\n", await File.ReadAllTextAsync(sideEffect, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokerSettingsWrite_UsesActualSessionDiffApproval(bool allowed)
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var answerer = allowed ? ApprovalAnswerers.AutoApprove(fixture.App.Ctx) : ApprovalAnswerers.DenyAll(fixture.App.Ctx);
        var before = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        var after = before.Replace("sandbox-test/old", "sandbox-test/new", StringComparison.Ordinal);
        Assert.NotEqual(before, after);

        var result = await fixture.Execute("alpha", "write", WriteArguments(fixture.Home.SettingsFile, after));

        Assert.True(result.IsError == !allowed, result is ToolExecutionResult.Failure failure ? failure.Error.Message : "Unexpected tool success.");
        Assert.Equal(allowed ? after : before, await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
        var asked = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
        Assert.Contains("sandbox-test/new", ApprovalHints.SettingsDiff(asked.Arguments));
        var audit = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<SettingsMutationAuditPayload>());
        Assert.Equal(allowed ? "committed" : "rejected", audit.Outcome);
        Assert.Equal(audit.CallId, asked.CallId);
        Assert.Equal(allowed ? 1 : 0, fixture.Session.SnapshotEvents().Count(item => item.Data is FileChangedPayload));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ExpiredInvocation_BackgroundWorkCannotBorrowLaterCallsSettingsAuthority(bool sandbox, bool hostRole)
    {
        using var fixture = new Fixture(settings =>
        {
            settings.Sandbox = new SandboxSettings { Enabled = sandbox };
            if (hostRole)
                foreach (var package in new[] { Alpha, Beta })
                    settings.Plugins[package] = settings.Plugins[package] with { Execution = PluginExecution.Host };
        });
        await fixture.StartAsync();
        using var answerer = ApprovalAnswerers.AutoApprove(fixture.App.Ctx);
        var identity = await fixture.Success("alpha", "identity");
        var instance = identity.GetProperty("instance").GetGuid();
        if (sandbox && !hostRole)
            fixture.AssertNoMainExecution();
        else
            Assert.Equal(Environment.ProcessId, identity.GetProperty("processId").GetInt32());
        var before = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        var after = before.Replace("sandbox-test/old", "sandbox-test/background", StringComparison.Ordinal);
        Assert.NotEqual(before, after);
        var armed = await fixture.Success("alpha", "arm-background-write", WriteArguments(fixture.Home.SettingsFile, after));
        Assert.True(armed.GetProperty("armed").GetBoolean());

        // The arming tool has returned. This second invocation releases a task that inherited the expired first call.
        var background = await fixture.Success("alpha", "release-background-write");

        Assert.False(background.GetProperty("committed").GetBoolean());
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
        Assert.Empty(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<FileChangedPayload>());
        Assert.DoesNotContain(fixture.Session.SnapshotEvents(), item => item.Data is SettingsMutationAuditPayload { Outcome: "committed" });
        Assert.Equal(instance, (await fixture.Success("alpha", "identity")).GetProperty("instance").GetGuid());

        await fixture.Success("alpha", "write", WriteArguments(fixture.Home.SettingsFile, after));
        Assert.Equal(after, await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
        Assert.Equal(after, Decode(await fixture.Success("alpha", "read", new { path = fixture.Home.SettingsFile }), "data"));
        var asked = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
        var committed = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<SettingsMutationAuditPayload>(),
            audit => audit.Outcome == "committed");
        Assert.Equal(asked.CallId, committed.CallId);
        Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<FileChangedPayload>());
    }

    [Fact]
    public async Task BrokerHttp_DoesNotTransferImplicitCookiesBetweenPackages_ButPreservesExplicitHeaders()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var server = builder.Build();
        var observedCookies = new List<string>();
        server.MapPost("/issue", async context =>
        {
            context.Response.Headers.SetCookie = "sandbox-authority=alpha-only; Path=/";
            await context.Response.WriteAsync("cookie issued", context.RequestAborted);
        });
        server.MapPost("/observe", async context =>
        {
            observedCookies.Add(context.Request.Headers.Cookie.ToString());
            await context.Response.WriteAsync("request observed", context.RequestAborted);
        });
        await server.StartAsync(TestContext.Current.CancellationToken);
        var origin = server.Urls.Single();

        var issued = await fixture.Success("alpha", "http", new { url = origin + "/issue", method = "POST" });
        Assert.Equal("cookie issued", Decode(issued, "body"));
        var implicitRequest = await fixture.Success("beta", "http", new { url = origin + "/observe", method = "POST" });
        Assert.Equal("request observed", Decode(implicitRequest, "body"));
        Assert.Equal("", Assert.Single(observedCookies));

        var explicitRequest = await fixture.Success("beta", "http", new
        {
            url = origin + "/observe", method = "POST",
            headers = new Dictionary<string, string> { ["Cookie"] = "caller-authorized=beta-only" },
        });
        Assert.Equal("request observed", Decode(explicitRequest, "body"));
        Assert.Equal(new[] { "", "caller-authorized=beta-only" }, observedCookies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddIndependentArtifact_ApprovesBeforeInstall_AndNeverConstructsFileCodeInMain(bool allowed)
    {
        using var fixture = new Fixture(settings =>
        {
            foreach (var package in new[] { Alpha, Beta })
                settings.Plugins[package] = settings.Plugins[package] with { Enabled = false };
        }, deployFixture: false);
        await fixture.StartAsync(expectFixtureActive: false);
        using var answerer = allowed ? ApprovalAnswerers.AutoApprove(fixture.App.Ctx) : ApprovalAnswerers.DenyAll(fixture.App.Ctx);
        var before = await File.ReadAllBytesAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        TestContext.Current.TestOutputHelper?.WriteLine("plugin-add stage=invoking");

        var result = await fixture.ExecuteTool("plugin_manager", new { action = "add", target = fixture.Artifact }, TestContext.Current.CancellationToken);
        var events = fixture.Session.SnapshotEvents().Select(item => item.Data).ToArray();
        var approvals = events.OfType<ApprovalAskedPayload>().ToArray();
        var decisions = events.OfType<ApprovalDecidedPayload>().ToArray();
        var audits = events.OfType<SettingsMutationAuditPayload>().ToArray();
        var failure = result as ToolExecutionResult.Failure;
        var errorCategory = failure?.Error switch
        {
            null => "none",
            { Info.Code: ToolErrorCodes.Aborted or ToolErrorCodes.AbortedBeforeDispatch } => "cancelled",
            { Message: "Settings mutation was not approved (Rejected)." } => "settings-rejected",
            { Message: var message } when message.StartsWith("The isolated worker could not start:", StringComparison.Ordinal) => "worker-start",
            { Message: var message } when message.StartsWith("Plugin inspection failed for ", StringComparison.Ordinal) => "inspection",
            _ => "other",
        };
        var stage = approvals.Length == 0 ? "before-approval" : audits.Length == 0 ? "approval-without-audit" : "audit-recorded";
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"plugin-add stage={stage} error={errorCategory} approvals={approvals.Length} decisions={decisions.Length} audits={audits.Length} elapsed-ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");

        Assert.Equal(!allowed, result.IsError);
        fixture.AssertNoMainExecution();
        Assert.Equal(allowed, HarnessSettings.Load(fixture.Home).Plugins[Alpha].Enabled);
        Assert.Equal(allowed, HarnessSettings.Load(fixture.Home).Plugins[Beta].Enabled);
        var installed = Directory.Exists(fixture.Plugins)
            ? Directory.GetFiles(fixture.Plugins, "Dsh.IsolatedPluginSample.dll", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.Equal(allowed ? 1 : 0, installed.Length);
        var asked = Assert.Single(approvals);
        using var approvalArguments = JsonDocument.Parse(asked.Arguments!);
        Assert.True(approvalArguments.RootElement.GetProperty("settingsMutation").GetBoolean());
        var decided = Assert.Single(decisions);
        Assert.Equal(asked.Id, decided.Id);
        Assert.Equal(allowed ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected, decided.Outcome);
        var audit = Assert.Single(audits);
        Assert.Equal(allowed ? "committed" : "rejected", audit.Outcome);
        Assert.Equal(asked.CallId, audit.CallId);
        if (allowed)
        {
            Assert.Equal(await fixture.Instance("alpha"), await fixture.Instance("beta"));
            var target = Path.Combine(fixture.Root, "installed-plugin-effect.txt");
            await fixture.Success("alpha", "write", WriteArguments(target, "installed capability is active"));
            Assert.Equal("installed capability is active", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
            Assert.Null(fixture.App.Composition!.Find(Alpha));
            Assert.Null(fixture.App.Composition.Find(Beta));
        }
        fixture.AssertNoMainExecution();
    }

    [Fact]
    public async Task CompiledFullContextPlugin_RequiresTheSameExplicitHostRole()
    {
        const string package = "@deepseek-ai/dsh-core";
        using var fixture = new Fixture(settings =>
            settings.Plugins[package] = settings.Plugins[package] with { Execution = null });
        var error = await Assert.ThrowsAsync<RuntimeException>(() => fixture.StartAsync());
        Assert.Equal("PLUGIN_HOST_ROLE_REQUIRED", error.Code);
        Assert.Contains(package, error.Message);
        Assert.Contains("execution: host", error.Message);
        fixture.AssertNoMainExecution();
    }

    [Fact]
    public async Task LegacyFileAndCompiledPackage_WithoutHostRoleRefuseStartup_AndNeverLoadFileInMain()
    {
        using var fixture = new Fixture(settings => settings.Plugins["test/local"] = new PluginSetting
        {
            Execution = PluginExecution.Sandbox,
        });
        var legacyDirectory = Path.Combine(fixture.Plugins, "legacy");
        Directory.CreateDirectory(legacyDirectory);
        foreach (var suffix in new[] { ".dll", ".deps.json" })
        {
            var name = "Dsh.ManagedPluginSample" + suffix;
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(legacyDirectory, name));
        }
        var loadedInMain = false;
        AssemblyLoadEventHandler observe = (_, change) =>
        {
            if (change.LoadedAssembly.GetName().Name == "Dsh.ManagedPluginSample")
                loadedInMain = true;
        };
        AppDomain.CurrentDomain.AssemblyLoad += observe;
        try
        {
            var error = await Assert.ThrowsAsync<RuntimeException>(() => fixture.StartAsync());
            Assert.Equal("PLUGIN_HOST_ROLE_REQUIRED", error.Code);
            Assert.False(loadedInMain);
        }
        finally { AppDomain.CurrentDomain.AssemblyLoad -= observe; }
    }

    private static object WriteArguments(string path, string text, bool append = false)
        => new { path, data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), append };

    private static string Decode(JsonElement result, string property)
        => Encoding.UTF8.GetString(Convert.FromBase64String(result.GetProperty(property).GetString()!));

    internal sealed class Fixture : IDisposable
    {
        private const string MainExecutionMarkerKey = "Dsh.IsolatedPluginSample.MainExecutionMarker";
        private readonly object? _previousMainExecutionMarker = AppContext.GetData(MainExecutionMarkerKey);
        private readonly string _mainExecutionMarker;
        private readonly bool _sandbox;
        private static readonly HashSet<string> EnabledImagePackages =
        [
            "@deepseek-ai/dsh-core", "@deepseek-ai/dsh-interaction", "@deepseek-ai/dsh-plugin-manager",
        ];
        private HarnessApp? _app;
        private AgentLoopAgent? _agent;
        private readonly CancellationTokenSource _deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        public string Root { get; } = TempTree.CreateDirectory("isolated-plugin");
        public HarnessHome Home { get; }
        public string Plugins { get; }
        public string Artifact { get; }
        public HarnessOptions Options { get; }
        public HarnessApp App => _app ?? throw new InvalidOperationException("Fixture is not started.");
        public Session Session { get; private set; } = null!;
        public ToolRuntime Tools => App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        public HarnessPluginManager Manager => App.Ctx.Get<HarnessPluginManager>("pluginManager")!;

        public Fixture(Action<HarnessSettings>? configure = null, bool deployFixture = true)
        {
            _deadline.CancelAfter(TimeSpan.FromMinutes(3));
            _mainExecutionMarker = Path.Combine(Root, "main-execution-marker");
            Home = HarnessHome.Resolve(Path.Combine(Root, "home"));
            Home.Ensure();
            Plugins = Path.Combine(Root, "plugins");
            var directory = deployFixture ? Path.Combine(Plugins, "Dsh.IsolatedPluginSample") : Path.Combine(Root, "uninstalled-fixture");
            Directory.CreateDirectory(directory);
            foreach (var suffix in new[] { ".dll", ".deps.json" })
            {
                var name = "Dsh.IsolatedPluginSample" + suffix;
                File.Copy(Path.Combine(AppContext.BaseDirectory, "isolated-plugin", name), Path.Combine(directory, name));
            }
            Artifact = Path.Combine(directory, "Dsh.IsolatedPluginSample.dll");
            foreach (var marker in Markers())
            {
                File.WriteAllText(marker, "Main has write access");
                Assert.Equal("Main has write access", File.ReadAllText(marker));
                File.Delete(marker);
            }
            var settings = HarnessSettings.Load(Home);
            settings.GlobalDefaultModel = "sandbox-test/old";
            settings.Sandbox = new SandboxSettings { Enabled = true };
            settings.Safety = new SafetySettings { AutoApprove = false, Blacklist = [], Whitelist = [] };
            // Host-role test inputs are explicit user configuration, independent of package source.
            foreach (var registration in DshPluginCatalogRegistry.Snapshot())
                settings.Plugins[registration.Package] = new PluginSetting
                {
                    Enabled = EnabledImagePackages.Contains(registration.Package),
                    Execution = PluginExecution.Host, Permissions = PluginPermissions.AllAllowed,
                };
            foreach (var package in new[] { Alpha, Beta })
                settings.Plugins[package] = new PluginSetting
                {
                    Execution = PluginExecution.Sandbox, Permissions = PluginPermissions.AllAllowed,
                };
            configure?.Invoke(settings);
            _sandbox = settings.Sandbox?.Enabled == true;
            settings.Save(Home);
            settings.SavePlugins(Home);
            Options = new HarnessOptions(Home, Cwd: Root, PluginsDirectory: Plugins);
            AppContext.SetData(MainExecutionMarkerKey, _mainExecutionMarker);
        }

        public async Task StartAsync(bool expectFixtureActive = true)
        {
            if (_sandbox)
                await SandboxTestBackend.RequireAsync();
            _app = await ConfigBoot.Compose(Options).WaitAsync(_deadline.Token);
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            Session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = Root, CreatedAt = 0, IsSeeded = false,
            });
            _agent = new AgentLoopAgent(App.Ctx, id, new AgentOptions(), Session, _ => 0);
            Session.Append(new TurnStartPayload(1));
            if (expectFixtureActive)
            {
                var failures = string.Join("; ", Manager.LoadFailures.Select(failure => failure.Reason));
                Assert.True(App.Composition!.Find(Alpha)?.State == ActivationState.Active, failures);
                Assert.True(App.Composition.Find(Beta)?.State == ActivationState.Active, failures);
            }
        }

        public Task<ToolExecutionResult> Execute(string package, string operation, object? arguments = null)
            => ExecuteTool("isolated_" + package, new { operation, arguments = arguments ?? new { } });

        public Task<ToolExecutionResult> ExecuteTool(string name, object arguments, CancellationToken cancellationToken = default)
            => Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Name = name,
                Arguments = JsonSerializer.SerializeToElement(arguments),
                Agent = _agent, Signal = cancellationToken.CanBeCanceled ? cancellationToken : _deadline.Token,
            }).WaitAsync(_deadline.Token);

        public async Task<JsonElement> Success(string package, string operation, object? arguments = null)
            => Assert.IsType<ToolExecutionResult.Success>(await Execute(package, operation, arguments)).Value;

        public async Task<JsonElement> Identity(string package)
        {
            var identity = await Success(package, "identity");
            var processId = identity.GetProperty("processId").GetInt32();
            Assert.Equal(processId, identity.GetProperty("constructorProcessId").GetInt32());
            Assert.Equal(processId, identity.GetProperty("initializationProcessId").GetInt32());
            if (OperatingSystem.IsLinux())
            {
                var workerNamespace = identity.GetProperty("pidNamespace").GetString();
                Assert.StartsWith("pid:[", workerNamespace);
                Assert.Equal(!_sandbox, new FileInfo("/proc/self/ns/pid").LinkTarget == workerNamespace);
            }
            else
                Assert.Equal(!_sandbox, Environment.ProcessId == processId);
            if (!_sandbox)
                Assert.True(File.Exists(_mainExecutionMarker));
            return identity;
        }

        public async Task<Guid> Instance(string package)
        {
            var instance = (await Identity(package)).GetProperty("instance").GetGuid();
            Assert.NotEqual(Guid.Empty, instance);
            return instance;
        }

        public async Task WaitForToolsToDisappear()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (Tools.Get("isolated_alpha") is not null || Tools.Get("isolated_beta") is not null)
                await Task.Delay(10, deadline.Token);
        }

        private IEnumerable<string> Markers()
        {
            yield return _mainExecutionMarker;
            foreach (var artifact in Directory.EnumerateFiles(Root, "Dsh.IsolatedPluginSample.dll", SearchOption.AllDirectories))
            {
                yield return artifact + ".module-marker";
                foreach (var package in new[] { "alpha", "beta" })
                {
                    yield return artifact + $".{package}-constructor-marker";
                    yield return artifact + $".{package}-initialize-marker";
                    yield return artifact + $".{package}-tool-marker";
                }
            }
        }

        public void AssertNoMainExecution()
        {
            // The process-local probe also detects execution through an image cached by an earlier sandbox-OFF fixture.
            foreach (var marker in Markers())
                Assert.False(File.Exists(marker));
        }

        public void Dispose()
        {
            try
            {
                _agent?.Dispose();
                _app?.Dispose();
            }
            finally
            {
                _deadline.Dispose();
                AppContext.SetData(MainExecutionMarkerKey, _previousMainExecutionMarker);
                TempTree.Delete(Root);
            }
        }
    }
}
