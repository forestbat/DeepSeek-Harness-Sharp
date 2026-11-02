using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Tools;

namespace Dsh.Tests;

public sealed class SettingsMutationTests
{
    [Theory]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("str_replace_editor")]
    public async Task RejectedRealFileTools_LeaveSettingsUnchanged_AndAuditTheProposal(string tool)
    {
        using var fixture = new Fixture();
        using var deny = ApprovalAnswerers.DenyAll(fixture.Context);
        var result = await fixture.Execute(tool, fixture.Arguments(tool));

        Assert.True(result.IsError);
        Assert.Equal(Fixture.Original, File.ReadAllText(fixture.Home.SettingsFile));
        var audit = Assert.Single(fixture.Audits);
        Assert.Equal("rejected", audit.Outcome);
        Assert.Equal(tool, audit.ToolName);
        Assert.Equal(audit.CallId, audit.RootCallId);
        Assert.Equal(Dsh.Sandbox.SandboxFileAccess.Canonicalize(fixture.Home.SettingsFile), audit.Path);
        Assert.DoesNotContain("credential-old-for-test", audit.Diff);
        Assert.Contains("alpha/new", audit.Diff);
        Assert.Empty(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<FileChangedPayload>());
        var asked = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
        Assert.Equal(audit.CallId, asked.CallId);
        Assert.DoesNotContain("credential-old-for-test", asked.Arguments);
        Assert.Contains("+global_default_model: alpha/new", ApprovalHints.SettingsDiff(asked.Arguments));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("str_replace_editor")]
    public async Task ApprovedRealFileTools_CommitOnce_WithRedactedDiffAndOwnerOnlyPublication(string tool)
    {
        using var fixture = new Fixture();
        using var allow = ApprovalAnswerers.AutoApprove(fixture.Context);
        using var previous = new StreamReader(new FileStream(fixture.Home.SettingsFile, FileMode.Open,
            FileAccess.Read, FileShare.Read | FileShare.Delete));
        var result = await fixture.Execute(tool, fixture.Arguments(tool));

        Assert.False(result.IsError, string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Equal(Fixture.Original.Replace("alpha/old", "alpha/new"), File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Equal(Fixture.Original, await previous.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal("committed", Assert.Single(fixture.Audits).Outcome);
        Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
        Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<FileChangedPayload>());
        Assert.DoesNotContain("credential-old-for-test", result.Meta?.GetRawText());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Home.SettingsFile));
    }

    [Fact]
    public async Task RealFileProposal_ReleasesLock_AndRejectsConcurrentHumanChange()
    {
        using var fixture = new Fixture();
        var asked = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remove = fixture.Context.OnWaterfall<ApprovalRequestNotification>((notification, _) =>
        {
            asked.TrySetResult(notification.Request);
            return new(answer.Task);
        }, new EventOptions { Global = true });
        try
        {
            var pending = fixture.Execute("write", fixture.Arguments("write"));
            await asked.Task.WaitAsync(TestContext.Current.CancellationToken);
            HarnessSettings.Update(fixture.Home, settings =>
            {
                settings.CompactionModel = "human/current";
                return true;
            });
            answer.SetResult(ApprovalOutcome.AllowedOnce);
            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(result.IsError);
            var actual = HarnessSettings.Load(fixture.Home);
            Assert.Equal("alpha/old", actual.GlobalDefaultModel);
            Assert.Equal("human/current", actual.CompactionModel);
            Assert.Equal("failed", Assert.Single(fixture.Audits).Outcome);
            Assert.Contains("# keep this comment", File.ReadAllText(fixture.Home.SettingsFile));
        }
        finally
        {
            answer.TrySetResult(ApprovalOutcome.Cancelled);
            remove();
        }
    }

    [Fact]
    public async Task CancelledProposal_NeverPublishes_AndAuditsCancellation()
    {
        using var fixture = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remove = fixture.Context.OnWaterfall<ApprovalRequestNotification>((_, _) =>
        {
            asked.TrySetResult();
            return new(answer.Task);
        }, new EventOptions { Global = true });
        try
        {
            var pending = fixture.Execute("write", fixture.Arguments("write"), cancellation.Token);
            await asked.Task.WaitAsync(TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            Assert.True((await pending.WaitAsync(TestContext.Current.CancellationToken)).IsError);
            Assert.Equal(Fixture.Original, File.ReadAllText(fixture.Home.SettingsFile));
            Assert.Equal("cancelled", Assert.Single(fixture.Audits).Outcome);
        }
        finally
        {
            answer.TrySetResult(ApprovalOutcome.Cancelled);
            remove();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlacklistWinsOverWhitelistAndAutoApprove(bool autoApprove)
    {
        using var fixture = new Fixture();
        HarnessSettings.Update(fixture.Home, settings =>
        {
            settings.Safety = new SafetySettings { AutoApprove = autoApprove,
                Whitelist = new() { ["allow-write"] = new() { Tool = ["write"] } },
                Blacklist = new() { ["deny-write"] = new() { Tool = ["write"] } } };
            return true;
        });
        var before = File.ReadAllText(fixture.Home.SettingsFile);
        var result = await fixture.Execute("write", fixture.Arguments("write"));

        Assert.True(result.IsError);
        Assert.Equal(before, File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Contains(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<SafetyDecisionAuditPayload>(),
            audit => audit.RuleId == "deny-write" && audit.Decision == "deny");
    }

    [Fact]
    public async Task WhitelistMap_BypassesNotification_AndRecordsAuthoritativeDecision()
    {
        using var fixture = new Fixture();
        HarnessSettings.Update(fixture.Home, settings =>
        {
            settings.Safety = new SafetySettings { Whitelist = new() { ["allow-edit"] = new() { Tool = ["edit"] } } };
            return true;
        });
        var result = await fixture.Execute("edit", fixture.Arguments("edit"));

        Assert.False(result.IsError);
        Assert.Equal("alpha/new", HarnessSettings.Load(fixture.Home).GlobalDefaultModel);
        Assert.DoesNotContain(fixture.Session.SnapshotEvents(), item => item.Data is ApprovalAskedPayload);
        Assert.Contains(fixture.SafetyAudits, audit => audit.RuleId == "allow-edit" && audit.Decision == "allow");
        Assert.Equal("committed", Assert.Single(fixture.Audits).Outcome);
    }

    [Fact]
    public async Task MissingGate_RejectsActiveSettings_ButNotOrdinaryFileWrites()
    {
        using var fixture = new Fixture();
        fixture.Gate.Dispose();
        using var allow = ApprovalAnswerers.AutoApprove(fixture.Context);
        var result = await fixture.Execute("write", fixture.Arguments("write"));

        Assert.True(result.IsError);
        Assert.Equal(Fixture.Original, File.ReadAllText(fixture.Home.SettingsFile));
        var ordinary = Path.Combine(fixture.Root, "ordinary.txt");
        var ordinaryResult = await fixture.Execute("write", new JsonObject { ["file_path"] = ordinary, ["content"] = "synthetic ordinary content" });
        Assert.False(ordinaryResult.IsError);
        Assert.Equal("synthetic ordinary content", File.ReadAllText(ordinary));
    }

    [Fact]
    public async Task LinkedDirectoryAlias_UsesTheActiveSettingsGate()
    {
        using var fixture = new Fixture();
        var alias = Path.Combine(fixture.Root, "home-alias");
        try
        {
            Directory.CreateSymbolicLink(alias, fixture.Home.Root);
        }
        catch (Exception error) when (OperatingSystem.IsWindows()
            && error is IOException or UnauthorizedAccessException && (error.HResult & 0xffff) == 1314)
        {
            Assert.Skip("The test account lacks the Windows symbolic-link creation privilege.");
        }
        using var deny = ApprovalAnswerers.DenyAll(fixture.Context);
        var result = await fixture.Execute("write", new JsonObject
        {
            ["file_path"] = Path.Combine(alias, "settings.yaml"),
            ["content"] = Fixture.Original.Replace("alpha/old", "alpha/new"),
        });

        Assert.True(result.IsError);
        Assert.Equal("rejected", Assert.Single(fixture.Audits).Outcome);
        Assert.Equal(Fixture.Original, File.ReadAllText(fixture.Home.SettingsFile));
    }


    [Theory]
    [InlineData("failure")]
    [InlineData("reentrant")]
    public async Task ApprovedCommitFailure_IsAudited_WithoutReentrantMutexDeadlock(string method)
    {
        using var fixture = new Fixture();
        using var allow = ApprovalAnswerers.AutoApprove(fixture.Context);
        var run = new ToolRunContext
        {
            CallId = ToolCallId.Create("synthetic-settings-failure"),
            RootCallIdValue = ToolCallId.Create("synthetic-settings-failure"),
            Name = "write", Arguments = JsonSerializer.SerializeToElement(fixture.Arguments("write")),
            Agent = fixture.Agent, Signal = TestContext.Current.CancellationToken,
        };
        Task Commit() => fixture.Gate.CommitAsync(new(fixture.Home.SettingsFile, Fixture.Original,
            Fixture.Original.Replace("alpha/old", "alpha/new"), run), (_, signal) =>
        {
            if (method == "failure")
                return File.WriteAllTextAsync(Path.Combine(fixture.Home.SettingsFile, "cannot-be-a-directory"), "synthetic content", signal);
            HarnessSettings.Update(fixture.Home, value => { value.GlobalDefaultModel = "alpha/new"; return true; });
            return Task.CompletedTask;
        }, run.Signal);
        if (method == "failure")
            await Assert.ThrowsAnyAsync<IOException>(Commit);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(Commit);
        Assert.Equal(Fixture.Original, File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Equal("failed", Assert.Single(fixture.Audits).Outcome);
    }

    [Fact]
    public async Task NoAnswerer_FailsClosedForTheActualSettingsWrite()
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute("write", fixture.Arguments("write"));
        Assert.True(result.IsError);
        Assert.Equal(Fixture.Original, File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Equal("rejected", Assert.Single(fixture.Audits).Outcome);
    }


    [Fact]
    public async Task MissingApprovalServiceOrModelSession_NeverWritesSettings()
    {
        using (var fixture = new Fixture(withApproval: false))
        {
            var result = await fixture.Execute("write", fixture.Arguments("write"));
            Assert.True(result.IsError);
            Assert.Equal(Fixture.Original, File.ReadAllText(fixture.Home.SettingsFile));
            Assert.Equal("failed", Assert.Single(fixture.Audits).Outcome);
        }
        using (var fixture = new Fixture())
        {
            using var allow = ApprovalAnswerers.AutoApprove(fixture.Context);
            var result = await fixture.Execute("write", fixture.Arguments("write"), withAgent: false);
            Assert.True(result.IsError);
            Assert.Equal(Fixture.Original, File.ReadAllText(fixture.Home.SettingsFile));
        }
    }

    [Fact]
    public async Task NeverPolicy_StillRejectsWhitelistedSettingsMutation()
    {
        using var fixture = new Fixture();
        HarnessSettings.Update(fixture.Home, settings =>
        {
            settings.Safety = new SafetySettings { AutoApprove = true, Whitelist = new() { ["allow-edit"] = new() { Tool = ["edit"] } } };
            return true;
        });
        ApprovalService.SetApprovalPolicy(fixture.Session, ApprovalPolicy.Never);
        var before = File.ReadAllText(fixture.Home.SettingsFile);
        var result = await fixture.Execute("edit", fixture.Arguments("edit"));
        Assert.True(result.IsError);
        Assert.Equal(before, File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Contains(fixture.SafetyAudits, audit => audit.Tool == "edit" && audit.SessionId == fixture.Session.Id.Value
            && audit.Outcome == "rejected");
        Assert.Empty(fixture.Audits);
    }


    [Fact]
    public async Task LivePolicyEdits_RemoveAutomaticGrants_AndKeepBlacklistPrecedence()
    {
        using var fixture = new Fixture();
        void Configure(SafetySettings policy) => HarnessSettings.Update(fixture.Home, settings => { settings.Safety = policy; return true; });
        Task<ToolExecutionResult> WriteOrdinary(string name) => fixture.Execute("write", new JsonObject
        {
            ["file_path"] = Path.Combine(fixture.Session.WorkingDirectory!, name), ["content"] = "ordinary file",
        });

        Configure(new SafetySettings { Whitelist = new() { ["allow-write"] = new() { Tool = ["write"] } } });
        Assert.False((await WriteOrdinary("allowed.txt")).IsError);
        Configure(new SafetySettings { AutoApprove = true,
            Whitelist = new() { ["allow-write"] = new() { Tool = ["write"] } },
            Blacklist = new() { ["deny-write"] = new() { Tool = ["write"] } } });
        Assert.True((await WriteOrdinary("denied.txt")).IsError);
        Assert.False(File.Exists(Path.Combine(fixture.Session.WorkingDirectory!, "denied.txt")));
        Configure(new SafetySettings());
        Assert.True((await WriteOrdinary("no-longer-whitelisted.txt")).IsError);
        Assert.False(File.Exists(Path.Combine(fixture.Session.WorkingDirectory!, "no-longer-whitelisted.txt")));
        Configure(new SafetySettings { AutoApprove = true });
        Assert.False((await WriteOrdinary("auto-approved.txt")).IsError);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SecretOnlyChange_RequiresApproval_WithoutCopyingCredentials(bool tokenShaped, bool approve)
    {
        using var fixture = new Fixture();
        // Synthetic values exercise credential handling without using a real secret.
        var oldToken = tokenShaped ? $"sk-{new string('a', 24)}" : "credential-old-for-test";
        var newToken = tokenShaped ? $"sk-{new string('b', 24)}" : "credential-new-for-test";
        var before = Fixture.Original.Replace("credential-old-for-test", oldToken);
        var after = before.Replace(oldToken, newToken);
        await File.WriteAllTextAsync(fixture.Home.SettingsFile, before, TestContext.Current.CancellationToken);
        var remove = fixture.Context.OnWaterfall<ApprovalRequestNotification>((_, _) =>
            new(approve ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected), new EventOptions { Global = true });
        try
        {
            var result = await fixture.Execute("write",
                new JsonObject { ["file_path"] = fixture.Home.SettingsFile, ["content"] = after });
            Assert.Equal(approve, !result.IsError);
            Assert.Equal(approve ? after : before, await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
            var audit = Assert.Single(fixture.Audits);
            Assert.Equal(approve ? "committed" : "rejected", audit.Outcome);
            Assert.NotEqual(audit.BeforeHash, audit.AfterHash);
            var asked = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
            foreach (var surface in new[]
            {
                audit.Diff, asked.Arguments, result.Meta?.GetRawText() ?? "",
                result is ToolExecutionResult.Success success ? success.Value.GetRawText() : "",
            })
            {
                Assert.DoesNotContain(oldToken, surface);
                Assert.DoesNotContain(newToken, surface);
            }
            Assert.DoesNotContain("# keep this comment", audit.Diff);
            Assert.DoesNotContain("# keep this comment", asked.Arguments);
            Assert.Contains(audit.BeforeHash, ApprovalHints.SettingsDiff(asked.Arguments));
            Assert.Contains(audit.AfterHash, ApprovalHints.SettingsDiff(asked.Arguments));
        }
        finally
        {
            remove();
        }
    }

    private sealed class Fixture : IDisposable
    {
        public const string Original = "# keep this comment\nglobal_default_model: alpha/old\nproviders:\n  alpha:\n    type: openai-compatible\n    options:\n      apiKey: credential-old-for-test\nsafety:\n  autoApprove: false\n  blacklist: {}\n  whitelist: {}\n";
        public string Root { get; } = TempTree.CreateDirectory("settings-mutation");
        public Context Context { get; } = new();
        public HarnessHome Home { get; }
        public Session Session { get; }
        public AgentLoopAgent Agent { get; }
        public ToolRuntime Tools { get; }
        public SettingsMutationGate Gate { get; }
        private readonly List<IDisposable> _tools;

        public Fixture(bool withApproval = true, SafetySettings? safety = null)
        {
            Home = HarnessHome.Resolve(Path.Combine(Root, "home"));
            Directory.CreateDirectory(Home.Root);
            Directory.CreateDirectory(Path.Combine(Root, "workspace"));
            File.WriteAllText(Home.SettingsFile, Original);
            _ = new SystemPrompt(Context, new SystemPromptConfig());
            Tools = new ToolRuntime(Context);
            _ = new LlmRuntime(Context);
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            Session = Session.Create(id, null, new SessionHeader { Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = Path.Combine(Root, "workspace"), CreatedAt = 0, IsSeeded = false });
            Agent = new AgentLoopAgent(Context, id, new AgentOptions(), Session, _ => 0);
            Session.Append(new TurnStartPayload(1));
            if (withApproval)
                _ = ApprovalService.Register(Context);
            Context.SetOwn(ISettingsMutationGate.SettingsPathProperty, Home.SettingsFile);
            Gate = new SettingsMutationGate(Context, Home);
            _ = new SafetyBoundary(Context, HarnessSettings.CreatePolicyReader(Home));
            _tools = [WriteTool.Register(Context), EditTool.Register(Context), StrReplaceEditorTool.Register(Context)];
        }

        public IEnumerable<SettingsMutationAuditPayload> Audits => Session.SnapshotEvents().Select(item => item.Data).OfType<SettingsMutationAuditPayload>();
        public IEnumerable<SafetyDecisionAuditPayload> SafetyAudits => Session.SnapshotEvents().Select(item => item.Data).OfType<SafetyDecisionAuditPayload>();

        public JsonObject Arguments(string tool) => tool switch
        {
            "write" => new() { ["file_path"] = Home.SettingsFile, ["content"] = Original.Replace("alpha/old", "alpha/new") },
            "edit" => new() { ["file_path"] = Home.SettingsFile, ["old_string"] = "alpha/old", ["new_string"] = "alpha/new" },
            _ => new() { ["command"] = "str_replace", ["path"] = Home.SettingsFile, ["old_str"] = "alpha/old", ["new_str"] = "alpha/new" },
        };

        public Task<ToolExecutionResult> Execute(string tool, JsonObject arguments, CancellationToken? signal = null, bool withAgent = true)
            => Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Name = tool,
                Arguments = JsonSerializer.SerializeToElement(arguments), Agent = withAgent ? Agent : null,
                Signal = signal ?? TestContext.Current.CancellationToken,
            });

        public void Dispose()
        {
            foreach (var tool in _tools)
                tool.Dispose();
            Gate.Dispose();
            Agent.Dispose();
            TempTree.Delete(Root);
        }
    }
}
