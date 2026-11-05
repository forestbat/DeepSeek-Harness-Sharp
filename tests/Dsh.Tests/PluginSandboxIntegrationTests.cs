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
    public async Task BrokerSettingsAppend_ReadDenyRunsWithoutAmbientScopeBeforeDiffAndActualCommit()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        HarnessSettings.Update(fixture.Home, settings =>
        {
            settings.Safety!.Blacklist["deny-snapshot-read"] = new() { Path = fixture.Home.SettingsFile, Intent = "read" };
            return true;
        });
        var expected = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        const string suffix = "\n# authorized broker append\n";
        var denied = await fixture.Execute("alpha", "write", WriteArguments(fixture.Home.SettingsFile, suffix, append: true));
        Assert.True(denied.IsError);
        Assert.Equal(expected, await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<SettingsMutationAuditPayload>().Select(audit => audit.Id));
        Assert.Contains(fixture.Session.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload
            { RuleId: "deny-snapshot-read", Decision: "deny", Intent: "read", Tool: "isolated_alpha" });
        HarnessSettings.Update(fixture.Home, settings => { settings.Safety!.Blacklist.Remove("deny-snapshot-read"); return true; });
        var before = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        var offset = fixture.Session.SnapshotEvents().Count;
        var allowed = await fixture.Execute("alpha", "write", WriteArguments(fixture.Home.SettingsFile, suffix, append: true));
        Assert.False(allowed.IsError);
        Assert.Equal(before + suffix, await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
        var events = fixture.Session.SnapshotEvents().Skip(offset).Select(item => item.Data).ToArray();
        Assert.Equal("committed", Assert.Single(events.OfType<SettingsMutationAuditPayload>()).Outcome);
        var audits = events.OfType<SafetyDecisionAuditPayload>().ToArray();
        Assert.Contains(audits, audit => audit.Intent == "read" && audit.Path == Dsh.Runtime.CanonicalFilePath.Canonicalize(fixture.Home.SettingsFile));
        Assert.Contains(audits, audit => audit.Intent == "write" && audit.Path == Dsh.Runtime.CanonicalFilePath.Canonicalize(fixture.Home.SettingsFile));
        Assert.All(audits, audit => { Assert.Equal(fixture.Session.Id.Value, audit.SessionId); Assert.Equal("isolated_alpha", audit.Tool); });
        Assert.NotNull(Assert.Single(audits.Select(audit => audit.CallId).Distinct()));
    }

    [Fact(Timeout = 60000)]
    public async Task BrokerHttp_NetworkRevocationDuringOperationApprovalPreventsTheActualRequest()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        await using var server = await PluginSdkHttpServer.StartAsync();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = 0;
        var approvalRequests = 0;
        var stopAdmission = fixture.App.Ctx.OnWaterfall<ToolPreExecuteNotification>((notification, _) =>
        {
            if (notification.Run.Name == "isolated_alpha" && Interlocked.Exchange(ref changed, 1) == 0)
                HarnessSettings.Update(fixture.Home, settings => { settings.Safety!.AutoApprove = false; return true; });
            return ValueTask.FromResult<object?>(new PreToolDecision.Allow());
        }, new EventOptions { Global = true });
        var stopApproval = fixture.App.Ctx.OnWaterfall<ApprovalRequestNotification>((_, _) =>
        {
            Interlocked.Increment(ref approvalRequests);
            asked.TrySetResult();
            return new(answer.Task);
        }, new EventOptions { Global = true });
        try
        {
            var pending = fixture.Execute("alpha", "http", new
            {
                url = server.Url, method = "POST", body = Convert.ToBase64String(Encoding.UTF8.GetBytes("must not reach origin")),
            });
            await asked.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref changed));
            Assert.Equal(0, server.Requests);
            HarnessSettings.UpdatePlugin(fixture.Home, Alpha, current =>
                (current ?? throw new InvalidOperationException("The real fixture plugin must exist.")) with
                { Permissions = new PluginPermissions { Read = true, Write = true, Network = false } });
            Assert.False(HarnessSettings.Load(fixture.Home).Plugins[Alpha].Permissions!.Network);
            answer.SetResult(ApprovalOutcome.AllowedOnce);
            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, server.Requests);
            Assert.True(result.IsError);
            Assert.Equal(1, Volatile.Read(ref approvalRequests));
        }
        finally
        {
            answer.TrySetResult(ApprovalOutcome.Cancelled);
            stopAdmission();
            stopApproval();
        }
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedWorkerCall_RetiresSharedWorker_AndExplicitEnableDoesNotReplay(bool cancel)
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var previous = await fixture.Instance("alpha");
        var marker = Path.Combine(fixture.Root, "before-failure.txt");
        ToolExecutionResult failed;
        if (cancel)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var pending = fixture.ExecuteTool("isolated_alpha", new
            {
                operation = "wait-after-write", arguments = WriteArguments(marker, "exactly once\n", append: true),
            }, cancellation.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker))
                await Task.Delay(10, deadline.Token);
            cancellation.Cancel();
            failed = await pending;
        }
        else
            failed = await fixture.Execute("alpha", "crash-after-write", WriteArguments(marker, "exactly once\n", append: true));
        Assert.True(failed.IsError);
        await fixture.WaitForToolsToDisappear();
        Assert.Equal("exactly once\n", await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken));

        await fixture.Manager.EnableAsync(Alpha);

        var restarted = await fixture.Instance("alpha");
        Assert.NotEqual(previous, restarted);
        Assert.Equal(restarted, await fixture.Instance("beta"));
        var target = Path.Combine(fixture.Root, "after-recovery.txt");
        await fixture.Success("beta", "write", WriteArguments(target, "new explicit invocation"));
        Assert.Equal("new explicit invocation", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal("exactly once\n", await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PermissionChanges_ApplyToNextBrokerOperation_WithoutRebuildingPackages()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var alpha = await fixture.Identity("alpha");
        var beta = await fixture.Identity("beta");
        Assert.Equal(1, (await fixture.Success("beta", "increment-state")).GetProperty("state").GetInt32());
        var source = Path.Combine(fixture.Root, "readable.txt");
        var target = Path.Combine(fixture.Root, "permission-effects.txt");
        await File.WriteAllTextAsync(source, "read allowed", TestContext.Current.CancellationToken);
        await using var server = await PluginSdkHttpServer.StartAsync();
        var request = new { url = server.Url, method = "POST" };
        foreach (var allowed in new[] { false, true })
        {
            await fixture.Manager.SetPermissionAsync(Alpha, PluginPermission.Read, allowed, TestContext.Current.CancellationToken);
            await fixture.Manager.SetPermissionAsync(Alpha, PluginPermission.Write, allowed, TestContext.Current.CancellationToken);
            await fixture.Manager.SetPermissionAsync(Alpha, PluginPermission.Network, allowed, TestContext.Current.CancellationToken);
            Assert.Equal(!allowed, (await fixture.Execute("alpha", "read", new { path = source })).IsError);
            Assert.Equal(!allowed, (await fixture.Execute("alpha", "write", WriteArguments(target, "alpha write"))).IsError);
            Assert.Equal(!allowed, (await fixture.Execute("alpha", "http", request)).IsError);
            if (allowed)
                Assert.Equal("alpha write", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
            else
                Assert.False(File.Exists(target));
            Assert.Equal(alpha.GetProperty("pluginInstance").GetGuid(), (await fixture.Identity("alpha")).GetProperty("pluginInstance").GetGuid());
            var sibling = await fixture.Identity("beta");
            Assert.Equal(beta.GetProperty("pluginInstance").GetGuid(), sibling.GetProperty("pluginInstance").GetGuid());
            Assert.Equal(beta.GetProperty("instance").GetGuid(), sibling.GetProperty("instance").GetGuid());
            Assert.Equal(beta.GetProperty("processId").GetInt32(), sibling.GetProperty("processId").GetInt32());
        }
        Assert.Equal(1, server.Requests);
        Assert.Equal(2, (await fixture.Success("beta", "increment-state")).GetProperty("state").GetInt32());
        await fixture.Success("beta", "write", WriteArguments(target, "sibling remains writable"));
        Assert.Equal("sibling remains writable", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IndependentUnload_ReleasesOwnedResources_AndKeepsSiblingStateAndAuthority()
    {
        using var fixture = new Fixture();
        // Deployed assets are readable on every backend; macOS does not grant worker scratch writes.
        const string localResourceName = "owned-resource.bin";
        var localResourcePath = Path.Combine(Path.GetDirectoryName(fixture.Artifact)!, localResourceName);
        await File.WriteAllTextAsync(localResourcePath, "owned resource", TestContext.Current.CancellationToken);
        await fixture.StartAsync();
        var alpha = await fixture.Identity("alpha");
        var beta = await fixture.Identity("beta");
        Assert.Equal(1, (await fixture.Success("beta", "increment-state")).GetProperty("state").GetInt32());
        Assert.True((await fixture.Success("alpha", "open-local-resource", new { relativePath = localResourceName }))
            .GetProperty("opened").GetBoolean());
        var before = await fixture.Success("beta", "probe-local-resource", new { package = Alpha });
        Assert.True(before.GetProperty("exists").GetBoolean());
        Assert.True(before.GetProperty("open").GetBoolean());

        var removed = await fixture.App.Composition!.Root.Scheduler.UnloadAsync(Alpha);
        await fixture.App.Composition.Root.Scheduler.SettleAsync();

        Assert.Equal(ActivationState.Disposed, removed!.State);
        Assert.Null(fixture.Tools.Get("isolated_alpha"));
        var after = await fixture.Success("beta", "probe-local-resource", new { package = Alpha });
        Assert.True(after.GetProperty("exists").GetBoolean());
        Assert.False(after.GetProperty("open").GetBoolean());
        Assert.Equal("owned resource", await File.ReadAllTextAsync(localResourcePath, TestContext.Current.CancellationToken));
        var sibling = await fixture.Identity("beta");
        Assert.Equal(beta.GetProperty("pluginInstance").GetGuid(), sibling.GetProperty("pluginInstance").GetGuid());
        Assert.Equal(beta.GetProperty("instance").GetGuid(), sibling.GetProperty("instance").GetGuid());
        Assert.Equal(beta.GetProperty("processId").GetInt32(), sibling.GetProperty("processId").GetInt32());
        Assert.Equal(2, (await fixture.Success("beta", "increment-state")).GetProperty("state").GetInt32());
        var target = Path.Combine(fixture.Root, "sibling-after-release.txt");
        await fixture.Success("beta", "write", WriteArguments(target, "still alive"));
        Assert.Equal("still alive", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));

        await fixture.Manager.EnableAsync(Alpha);

        var reloaded = await fixture.Identity("alpha");
        Assert.NotEqual(alpha.GetProperty("pluginInstance").GetGuid(), reloaded.GetProperty("pluginInstance").GetGuid());
        Assert.Equal(beta.GetProperty("instance").GetGuid(), reloaded.GetProperty("instance").GetGuid());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokerSettingsWrite_UsesActualSessionDiffApproval(bool allowed)
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var answerer = fixture.AnswerSettingsDiff(allowed);
        var before = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        var after = before.Replace("sandbox-test/old", "sandbox-test/new", StringComparison.Ordinal);
        Assert.NotEqual(before, after);

        var result = await fixture.Execute("alpha", "write", WriteArguments(fixture.Home.SettingsFile, after));

        Assert.True(result.IsError == !allowed, result is ToolExecutionResult.Failure failure ? failure.Error.Message : "Unexpected tool success.");
        Assert.Equal(allowed ? after : before, await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
        var asked = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>(),
            request => ApprovalHints.SettingsDiff(request.Arguments).Length != 0);
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
        using var answerer = fixture.AnswerSettingsDiff(allowed: true);
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
        Assert.DoesNotContain(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>(),
            request => ApprovalHints.SettingsDiff(request.Arguments).Length != 0);
        Assert.Empty(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<FileChangedPayload>());
        Assert.DoesNotContain(fixture.Session.SnapshotEvents(), item => item.Data is SettingsMutationAuditPayload { Outcome: "committed" });
        Assert.Equal(instance, (await fixture.Success("alpha", "identity")).GetProperty("instance").GetGuid());

        await fixture.Success("alpha", "write", WriteArguments(fixture.Home.SettingsFile, after));
        Assert.Equal(after, await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
        Assert.Equal(after, Decode(await fixture.Success("alpha", "read", new { path = fixture.Home.SettingsFile }), "data"));
        var asked = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>(),
            request => ApprovalHints.SettingsDiff(request.Arguments).Length != 0);
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
        using var answerer = fixture.AnswerSettingsDiff(allowed);
        var before = await File.ReadAllBytesAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        var result = await fixture.ExecuteTool("plugin_manager", new { action = "add", target = fixture.Artifact }, TestContext.Current.CancellationToken);
        var events = fixture.Session.SnapshotEvents().Select(item => item.Data).ToArray();
        var approvals = events.OfType<ApprovalAskedPayload>()
            .Where(request => ApprovalHints.SettingsDiff(request.Arguments).Length != 0).ToArray();
        var decisions = events.OfType<ApprovalDecidedPayload>().ToArray();
        var audits = events.OfType<SettingsMutationAuditPayload>().ToArray();
        Assert.Equal(!allowed, result.IsError);
        fixture.AssertNoMainExecution();
        Assert.Equal(allowed, HarnessSettings.Load(fixture.Home).Plugins[Alpha].Enabled);
        Assert.Equal(allowed, HarnessSettings.Load(fixture.Home).Plugins[Beta].Enabled);
        var asked = Assert.Single(approvals);
        using var approvalArguments = JsonDocument.Parse(asked.Arguments!);
        Assert.True(approvalArguments.RootElement.GetProperty("settingsMutation").GetBoolean());
        var decided = Assert.Single(decisions, decision => decision.Id == asked.Id);
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
    public async Task DefaultTemplate_WithSandboxEnabled_StartsWithoutPluginOverrides()
    {
        const string package = "@deepseek-ai/dsh-core";
        var root = TempTree.CreateDirectory("sandbox-default-template");
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(root, "home"));
            home.Ensure();
            var settings = HarnessSettings.Load(home);
            settings.Sandbox = new SandboxSettings { Enabled = true };
            settings.Save(home);

            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: root, PluginsDirectory: Path.Combine(root, "plugins")));

            Assert.Empty(app.Ctx.Get<HarnessPluginManager>("pluginManager")!.LoadFailures);
            Assert.Equal(ActivationState.Active, app.Composition!.Find(package)!.State);
        }
        finally { TempTree.Delete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddFullContextFile_RequiresHostBeforeApprovalConfigurationOrInstallation(bool initiallyEnabled)
    {
        using var fixture = new Fixture(settings => settings.Plugins["test/local"] = new PluginSetting { Enabled = initiallyEnabled });
        await fixture.StartAsync();
        var identity = await fixture.Identity("beta");
        var before = await File.ReadAllBytesAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        using var answerer = ApprovalAnswerers.AutoApprove(fixture.App.Ctx);
        var source = Path.Combine(fixture.Root, "full-context-observation");
        Directory.CreateDirectory(source);
        foreach (var suffix in new[] { ".dll", ".deps.json" })
        {
            var name = "Dsh.ManagedPluginSample" + suffix;
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(source, name));
        }

        var result = await fixture.ExecuteTool("plugin_manager", new
        {
            action = "add", target = Path.Combine(source, "Dsh.ManagedPluginSample.dll"),
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
        Assert.Equal(initiallyEnabled, HarnessSettings.Load(fixture.Home).Plugins["test/local"].Enabled);
        Assert.Empty(Directory.GetFiles(fixture.Plugins, "Dsh.ManagedPluginSample.dll", SearchOption.AllDirectories));
        Assert.Empty(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
        Assert.Empty(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<SettingsMutationAuditPayload>());
        Assert.Equal(identity.GetProperty("pluginInstance").GetGuid(), (await fixture.Identity("beta")).GetProperty("pluginInstance").GetGuid());
    }

    [Fact]
    public async Task FailedInspectionAndRejectedAdd_LeaveSettingsAndLiveSiblingUnchanged()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var identity = await fixture.Identity("beta");
        var broken = Path.Combine(fixture.Root, "broken.dll");
        await File.WriteAllTextAsync(broken, "not a plugin", TestContext.Current.CancellationToken);

        Assert.True((await fixture.ExecuteTool("plugin_manager", new { action = "add", target = broken }, TestContext.Current.CancellationToken)).IsError);

        await HarnessSettings.UpdatePluginAsync(fixture.Home, Alpha, existing => existing! with { Enabled = false });
        using var answerer = fixture.AnswerSettingsDiff(allowed: false);
        var before = await File.ReadAllBytesAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);

        Assert.True((await fixture.ExecuteTool("plugin_manager", new { action = "add", target = fixture.Artifact }, TestContext.Current.CancellationToken)).IsError);

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
        Assert.Contains(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<SettingsMutationAuditPayload>(),
            audit => audit.Outcome == "rejected");
        var sibling = await fixture.Identity("beta");
        Assert.Equal(identity.GetProperty("pluginInstance").GetGuid(), sibling.GetProperty("pluginInstance").GetGuid());
        Assert.Equal(identity.GetProperty("instance").GetGuid(), sibling.GetProperty("instance").GetGuid());
    }

    [Fact]
    public async Task DeployedResources_AreReadable_BusinessHomeIsBrokerOnly_AndRetirementPreservesSources()
    {
        using var fixture = new Fixture();
        var assets = Path.Combine(Path.GetDirectoryName(fixture.Artifact)!, "assets");
        Directory.CreateDirectory(assets);
        var resource = Path.Combine(assets, "message.txt");
        await File.WriteAllTextAsync(resource, "deployed resource", TestContext.Current.CancellationToken);
        var attributes = File.GetAttributes(resource);
        File.SetAttributes(resource, attributes | FileAttributes.ReadOnly);
        try
        {
            var shared = Path.Combine(fixture.Plugins, ".shared");
            Directory.CreateDirectory(shared);
            await File.WriteAllTextAsync(Path.Combine(shared, "resource.txt"), "shared resource", TestContext.Current.CancellationToken);
            var business = Path.Combine(fixture.Home.Root, "business-private.txt");
            await File.WriteAllTextAsync(business, "business data", TestContext.Current.CancellationToken);
            await fixture.StartAsync();

            Assert.Equal("deployed resource",
                (await fixture.Success("alpha", "read-resource", new { relativePath = "assets/message.txt" })).GetProperty("text").GetString());
            Assert.Equal("shared resource",
                (await fixture.Success("alpha", "read-resource", new { relativePath = "../.shared/resource.txt" })).GetProperty("text").GetString());
            Assert.False((await fixture.Success("alpha", "direct-read", new { path = business })).GetProperty("succeeded").GetBoolean());
            Assert.Equal("business data", Decode(await fixture.Success("alpha", "read", new { path = business }), "data"));

            await fixture.Manager.DisableAsync(Alpha);

            Assert.Equal("deployed resource", await File.ReadAllTextAsync(resource, TestContext.Current.CancellationToken));
            Assert.Equal("shared resource",
                (await fixture.Success("beta", "read-resource", new { relativePath = "../.shared/resource.txt" })).GetProperty("text").GetString());
        }
        finally { File.SetAttributes(resource, attributes); }
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
            settings.Safety = new SafetySettings { AutoApprove = true };
            foreach (var registration in DshPluginCatalogRegistry.Snapshot())
                settings.Plugins[registration.Package] = new PluginSetting
                {
                    Enabled = EnabledImagePackages.Contains(registration.Package),
                    Permissions = PluginPermissions.AllAllowed,
                };
            foreach (var package in new[] { Alpha, Beta })
                settings.Plugins[package] = new PluginSetting
                {
                    Permissions = PluginPermissions.AllAllowed,
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

        public IDisposable AnswerSettingsDiff(bool allowed)
        {
            HarnessSettings.Update(Home, settings => { settings.Safety!.AutoApprove = false; return true; });
            var unsubscribe = App.Ctx.OnWaterfall<ApprovalRequestNotification>((notification, _) =>
                ValueTask.FromResult<object?>(ApprovalHints.SettingsDiff(notification.Request.Arguments).Length == 0 || allowed
                    ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected), new EventOptions { Global = true });
            return new ApprovalSubscription(unsubscribe);
        }

        private sealed class ApprovalSubscription(Func<bool> unsubscribe) : IDisposable
        {
            public void Dispose() => unsubscribe();
        }

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
