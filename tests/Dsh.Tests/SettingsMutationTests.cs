using System.Runtime.InteropServices;
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
        Assert.Equal(Dsh.Transport.PinnedFileAccess.Canonicalize(fixture.Home.SettingsFile), audit.Path);
        Assert.DoesNotContain("credential-old-for-test", audit.Before);
        Assert.Contains("alpha/new", audit.After);
        Assert.Empty(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<FileChangedPayload>());
        var asked = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
        Assert.Equal(audit.CallId, asked.CallId);
        Assert.DoesNotContain("credential-old-for-test", asked.Arguments);
        Assert.Contains("+ global_default_model: alpha/new", ApprovalHints.SettingsDiff(asked.Arguments));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("str_replace_editor")]
    public async Task ApprovedRealFileTools_CommitOnce_WithRedactedDiffAndOwnerOnlyPublication(string tool)
    {
        using var fixture = new Fixture();
        using var allow = ApprovalAnswerers.AutoApprove(fixture.Context);
        var result = await fixture.Execute(tool, fixture.Arguments(tool));

        Assert.False(result.IsError);
        Assert.Equal(Fixture.Original.Replace("alpha/old", "alpha/new"), File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Equal("committed", Assert.Single(fixture.Audits).Outcome);
        Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
        Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<FileChangedPayload>());
        Assert.DoesNotContain("credential-old-for-test", result.Meta?.GetRawText());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Home.SettingsFile));
    }

    [Fact]
    public async Task TypedToolProposal_ReleasesLock_AndRejectsConcurrentHumanChange()
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
            var pending = Task.Run(() => fixture.Execute("typed_settings_update", new JsonObject()));
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
            settings.Safety = new SafetySettings { AutoApprove = autoApprove, Whitelist = ["tool:write"], Blacklist = ["tool:write"] };
            return true;
        });
        var before = File.ReadAllText(fixture.Home.SettingsFile);
        var result = await fixture.Execute("write", fixture.Arguments("write"));

        Assert.True(result.IsError);
        Assert.Equal(before, File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Equal("rejected", Assert.Single(fixture.Audits).Outcome);
    }

    [Fact]
    public async Task WhitelistUsesExistingToolGrammar_AndRecordsAuthoritativeDecision()
    {
        using var fixture = new Fixture();
        HarnessSettings.Update(fixture.Home, settings =>
        {
            settings.Safety = new SafetySettings { Whitelist = ["tool:edit"] };
            return true;
        });
        var result = await fixture.Execute("edit", fixture.Arguments("edit"));

        Assert.False(result.IsError);
        Assert.Equal("alpha/new", HarnessSettings.Load(fixture.Home).GlobalDefaultModel);
        var decision = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalDecidedPayload>());
        Assert.Equal(ApprovalOutcome.AllowedOnce, decision.Outcome);
    }

    [Fact]
    public async Task RootBoundary_RemainsFailClosed_AfterReviewerRemoval()
    {
        using var fixture = new Fixture();
        fixture.Reviewer.Dispose();
        using var allow = ApprovalAnswerers.AutoApprove(fixture.Context);
        var result = await fixture.Execute("write", fixture.Arguments("write"));

        Assert.True(result.IsError);
        Assert.Equal(Fixture.Original, File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Equal("unavailable", Assert.Single(fixture.Audits).Outcome);
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

    [Fact]
    public void RedactedDiff_HidesEscapedAndMultilineSecretsWithoutChangingOrdinaryComments()
    {
        const string before = "# ordinary comment\npassword: \"credential\\nold-for-test\"\nprivate_key: |\n  private material for test\n  second line\nvalue: old\n";
        var (oldText, newText) = SettingsMutationDiff.Redact(before, before.Replace("value: old", "value: new"));
        Assert.DoesNotContain("credential", oldText);
        Assert.DoesNotContain("private material", oldText);
        Assert.DoesNotContain("second line", newText);
        Assert.Contains("# ordinary comment", oldText);
        Assert.Contains("value: new", newText);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("plugins")]
    [InlineData("plugin-entry")]
    [InlineData("proxy")]
    [InlineData("borrowed")]
    public async Task TypedWritersInsideRealToolScope_RejectWithoutPublishing(string method)
    {
        using var fixture = new Fixture();
        using var deny = ApprovalAnswerers.DenyAll(fixture.Context);
        var result = await Task.Run(() => fixture.Execute("typed_settings_update", new JsonObject { ["method"] = method }));
        Assert.True(result.IsError);
        Assert.Equal(Fixture.Original, File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Equal("rejected", Assert.Single(fixture.Audits).Outcome);
        Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("reentrant")]
    public async Task ApprovedCommitFailure_IsAudited_WithoutReentrantMutexDeadlock(string method)
    {
        using var fixture = new Fixture();
        using var allow = ApprovalAnswerers.AutoApprove(fixture.Context);
        var result = await Task.Run(() => fixture.Execute("typed_settings_update", new JsonObject { ["method"] = method }))
            .WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
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
    public async Task OversizedUtf8Proposal_IsRejectedWithoutPersistingTheDocumentInAudit()
    {
        using var fixture = new Fixture();
        using var allow = ApprovalAnswerers.AutoApprove(fixture.Context);
        var result = await fixture.Execute("write", new JsonObject
        {
            ["file_path"] = fixture.Home.SettingsFile,
            ["content"] = new string('界', SettingsMutationLimits.MaxDocumentBytes / 3 + 1),
        });
        Assert.True(result.IsError);
        Assert.Equal(Fixture.Original, File.ReadAllText(fixture.Home.SettingsFile));
        var audit = Assert.Single(fixture.Audits);
        Assert.Equal("failed", audit.Outcome);
        Assert.True(audit.Before.Length < 256 && audit.After.Length < 256);
        Assert.Empty(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
    }

    [Fact]
    public void SecretOnlyChanges_RemainVisibleWithoutPersistingEitherValue()
    {
        var (before, after) = SettingsMutationDiff.Redact("password: old-value-for-test\n", "password: new-value-for-test\n");
        Assert.DoesNotContain("old-value-for-test", before);
        Assert.DoesNotContain("new-value-for-test", after);
        Assert.NotEqual(before, after);
        Assert.Contains("(changed)", after);
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
            settings.Safety = new SafetySettings { AutoApprove = true, Whitelist = ["tool:edit"] };
            return true;
        });
        ApprovalService.SetApprovalPolicy(fixture.Session, ApprovalPolicy.Never);
        var before = File.ReadAllText(fixture.Home.SettingsFile);
        var result = await fixture.Execute("edit", fixture.Arguments("edit"));
        Assert.True(result.IsError);
        Assert.Equal(before, File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Equal("rejected", Assert.Single(fixture.Audits).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardlinkAlias_UsesCanonicalApprovalAndPublication(bool allowed)
    {
        using var fixture = new Fixture();
        var alias = Path.Combine(fixture.Root, "settings-hardlink.yaml");
        if (OperatingSystem.IsWindows())
            Assert.True(CreateHardLinkWindows(alias, fixture.Home.SettingsFile, IntPtr.Zero), $"Hardlink creation failed: {Marshal.GetLastPInvokeError()}");
        else
            Assert.Equal(0, CreateHardLinkUnix(fixture.Home.SettingsFile, alias));
        using var answerer = allowed ? ApprovalAnswerers.AutoApprove(fixture.Context) : ApprovalAnswerers.DenyAll(fixture.Context);
        var result = await fixture.Execute("write", new JsonObject
        {
            ["file_path"] = alias,
            ["content"] = Fixture.Original.Replace("alpha/old", "alpha/new"),
        });
        Assert.Equal(!allowed, result.IsError);
        Assert.Equal(allowed ? "alpha/new" : "alpha/old", HarnessSettings.Load(fixture.Home).GlobalDefaultModel);
        var audit = Assert.Single(fixture.Audits);
        Assert.Equal(Dsh.Transport.PinnedFileAccess.Canonicalize(fixture.Home.SettingsFile), audit.Path);
        Assert.Equal(allowed ? "committed" : "rejected", audit.Outcome);
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existing, string link);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string link, string existing, IntPtr reserved);

    [Fact]
    public async Task LivePolicyEdits_RemoveAutomaticGrants_AndKeepBlacklistPrecedence()
    {
        using var fixture = new Fixture();
        SafetySettings? ReadSafety() => HarnessSettings.LoadForReview(fixture.Home).Safety;
        using var guard = SafetyCommandGuard.Register(fixture.Context, ReadSafety);
        using var approvals = SafetyCommandGuard.RegisterApprovals(fixture.Context, ReadSafety);
        void Configure(SafetySettings policy) => HarnessSettings.Update(fixture.Home, settings => { settings.Safety = policy; return true; });
        Task<ToolExecutionResult> WriteOrdinary(string name) => fixture.Execute("write", new JsonObject
        {
            ["file_path"] = Path.Combine(fixture.Root, name), ["content"] = "ordinary file",
        });

        Configure(new SafetySettings { Whitelist = ["tool:write"] });
        Assert.False((await WriteOrdinary("allowed.txt")).IsError);
        Configure(new SafetySettings { AutoApprove = true, Whitelist = ["tool:write"], Blacklist = ["tool:write"] });
        Assert.True((await WriteOrdinary("denied.txt")).IsError);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "denied.txt")));
        Configure(new SafetySettings());
        Assert.True((await WriteOrdinary("no-longer-whitelisted.txt")).IsError);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "no-longer-whitelisted.txt")));
        Configure(new SafetySettings { AutoApprove = true });
        Assert.False((await WriteOrdinary("auto-approved.txt")).IsError);
    }

    private sealed class Fixture : IDisposable
    {
        public const string Original = "# keep this comment\nglobal_default_model: alpha/old\nproviders:\n  alpha:\n    type: openai-compatible\n    options:\n      apiKey: credential-old-for-test\nsafety:\n  autoApprove: false\n  blacklist: []\n  whitelist: []\n";
        public string Root { get; } = TempTree.CreateDirectory("settings-mutation");
        public Context Context { get; } = new();
        public HarnessHome Home { get; }
        public Session Session { get; }
        public AgentLoopAgent Agent { get; }
        public ToolRuntime Tools { get; }
        public IDisposable Reviewer { get; }
        private readonly List<IDisposable> _tools;

        public Fixture(bool withApproval = true)
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
            var boundary = new SettingsMutationBoundary(Context, Home);
            Reviewer = boundary.InstallReviewer(new SettingsMutationGate(Context, Home));
            _tools = [WriteTool.Register(Context), EditTool.Register(Context), StrReplaceEditorTool.Register(Context)];
            _tools.Add(Tools.Register(new ToolDefinition
            {
                Name = "typed_settings_update", Description = "Update a real settings section through the typed writer.",
                Parameters = ToolSchemas.Parse("""{"type":"object","properties":{}}"""),
                Output = new ToolOutputDefinition(ToolSchemas.Parse("{\"type\":\"null\"}"), (_, _) => []),
                Execute = async (arguments, exec) =>
                {
                    var method = arguments.TryGetProperty("method", out var selected) ? selected.GetString() : "update";
                    switch (method)
                    {
                        case "save":
                            var settings = HarnessSettings.Load(Home);
                            settings.GlobalDefaultModel = "alpha/new";
                            settings.Save(Home);
                            break;
                        case "plugins":
                            var plugins = HarnessSettings.Load(Home);
                            plugins.Plugins["@tests/settings-gate"] = new PluginSetting { Enabled = false };
                            plugins.SavePlugins(Home);
                            break;
                        case "plugin-entry":
                            HarnessSettings.UpdatePlugin(Home, "@tests/settings-gate", _ => new PluginSetting { Enabled = false });
                            break;
                        case "proxy":
                            using (var proxy = new OutboundProxy())
                                HarnessSettings.SaveOutboundProxy(Home, new OutboundProxySettings { Url = "http://127.0.0.1:8080" }, proxy);
                            break;
                        case "borrowed":
                            HarnessSettings.SaveBorrowedOutboundMachine(Home, "test-machine");
                            break;
                        case "failure":
                        case "reentrant":
                            var before = File.ReadAllText(Home.SettingsFile);
                            var after = before.Replace("alpha/old", "alpha/new");
                            await boundary.CommitAsync(new(Home.SettingsFile, before, after, exec), signal =>
                            {
                                if (method == "failure")
                                    return File.WriteAllTextAsync(Path.Combine(Home.SettingsFile, "cannot-be-a-directory"), after, signal);
                                HarnessSettings.Update(Home, value => { value.GlobalDefaultModel = "alpha/new"; return true; });
                                return Task.CompletedTask;
                            }, exec.Signal);
                            break;
                        default:
                            HarnessSettings.Update(Home, value => { value.GlobalDefaultModel = "alpha/new"; return true; });
                            break;
                    }
                    return null;
                },
            }));
        }

        public IEnumerable<SettingsMutationAuditPayload> Audits => Session.SnapshotEvents().Select(item => item.Data).OfType<SettingsMutationAuditPayload>();

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
            Reviewer.Dispose();
            Agent.Dispose();
            TempTree.Delete(Root);
        }
    }
}
