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

        Assert.False(result.IsError, string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Equal(Fixture.Original.Replace("alpha/old", "alpha/new"), File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Equal("committed", Assert.Single(fixture.Audits).Outcome);
        Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
        Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<FileChangedPayload>());
        Assert.DoesNotContain("credential-old-for-test", result.Meta?.GetRawText());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Home.SettingsFile));
    }

    [Fact]
    public async Task ApprovedTypedSettingsUpdate_CommitsTheReviewedContent_WithRedactedAuditAndOwnerOnlyPublication()
    {
        using var fixture = new Fixture();
        using var allow = ApprovalAnswerers.AutoApprove(fixture.Context);
        var result = await fixture.Execute("typed_settings_update", new JsonObject());

        Assert.False(result.IsError, string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Equal(Fixture.Original.Replace("alpha/old", "alpha/new"), File.ReadAllText(fixture.Home.SettingsFile));
        var audit = Assert.Single(fixture.Audits);
        Assert.Equal("committed", audit.Outcome);
        Assert.Equal("typed_settings_update", audit.ToolName);
        Assert.Equal(audit.CallId, audit.RootCallId);
        Assert.Equal(Dsh.Transport.PinnedFileAccess.Canonicalize(fixture.Home.SettingsFile), audit.Path);
        Assert.Contains("alpha/old", audit.Before);
        Assert.Equal(audit.Before.Replace("alpha/old", "alpha/new"), audit.After);
        var asked = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
        Assert.Equal(audit.CallId, asked.CallId);
        Assert.NotNull(asked.Arguments);
        using var proposal = JsonDocument.Parse(asked.Arguments);
        Assert.Equal(audit.Path, proposal.RootElement.GetProperty("path").GetString());
        Assert.Equal(audit.Before, proposal.RootElement.GetProperty("before").GetString());
        Assert.Equal(audit.After, proposal.RootElement.GetProperty("after").GetString());
        Assert.DoesNotContain("credential-old-for-test", audit.Before);
        Assert.DoesNotContain("credential-old-for-test", audit.After);
        Assert.DoesNotContain("credential-old-for-test", asked.Arguments);
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
        Assert.True(result.IsError == !allowed, string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Equal(allowed ? "alpha/new" : "alpha/old", HarnessSettings.Load(fixture.Home).GlobalDefaultModel);
        Assert.Equal(allowed ? Fixture.Original.Replace("alpha/old", "alpha/new") : Fixture.Original,
            File.ReadAllText(fixture.Home.SettingsFile));
        Assert.Equal(Fixture.Original, File.ReadAllText(alias));
        var audit = Assert.Single(fixture.Audits);
        var asked = Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
        Assert.Equal(audit.CallId, asked.CallId);
        Assert.NotNull(asked.Arguments);
        using var proposal = JsonDocument.Parse(asked.Arguments);
        Assert.Equal(audit.Path, proposal.RootElement.GetProperty("path").GetString());
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

    [Fact]
    public async Task GlobReadDeny_CannotLeakThroughAHardlinkWithADifferentName()
    {
        var policy = new SafetySettings { AutoApprove = true,
            Blacklist = new() { ["private-name"] = new() { Path = "**/secret.txt", Intent = "read" } } };
        using var fixture = new Fixture(safety: policy);
        var secret = Path.Combine(fixture.Session.WorkingDirectory!, "secret.txt");
        var alias = Path.Combine(fixture.Session.WorkingDirectory!, "public.txt");
        var ordinary = Path.Combine(fixture.Session.WorkingDirectory!, "ordinary.txt");
        await File.WriteAllTextAsync(secret, "hardlink private content", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(ordinary, "single-link public content", TestContext.Current.CancellationToken);
        Assert.False((await fixture.Execute("read", new JsonObject { ["file_path"] = ordinary })).IsError);
        if (OperatingSystem.IsWindows()) Assert.True(CreateHardLinkWindows(alias, secret, IntPtr.Zero));
        else Assert.Equal(0, CreateHardLinkUnix(secret, alias));
        var denied = await fixture.Execute("read", new JsonObject { ["file_path"] = alias });
        Assert.True(denied.IsError);
        Assert.DoesNotContain("hardlink private content", string.Concat(denied.Content.OfType<TextBlock>().Select(block => block.Text)));
    }

    [Fact]
    public async Task PathCaseRules_FollowTheActualVolumeRatherThanTheOperatingSystem()
    {
        var policy = new SafetySettings { AutoApprove = true,
            Blacklist = new() { ["lowercase-private"] = new() { Path = "**/secret.txt", Intent = "read" } } };
        using var fixture = new Fixture(safety: policy);
        var lower = Path.Combine(fixture.Session.WorkingDirectory!, "secret.txt");
        var upper = Path.Combine(fixture.Session.WorkingDirectory!, "SECRET.TXT");
        await File.WriteAllTextAsync(lower, "lowercase private content", TestContext.Current.CancellationToken);
        var sameTarget = File.Exists(upper);
        if (!sameTarget) await File.WriteAllTextAsync(upper, "distinct uppercase public content", TestContext.Current.CancellationToken);
        var actual = await fixture.Execute("read", new JsonObject { ["file_path"] = upper });
        Assert.Equal(sameTarget, actual.IsError);
        var text = string.Concat(actual.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.DoesNotContain("lowercase private content", text);
        if (!sameTarget) Assert.Contains("distinct uppercase public content", text);
        Assert.True((await fixture.Execute("read", new JsonObject { ["file_path"] = lower })).IsError);
    }

    [Fact]
    public async Task UnicodeLiteralAliases_FollowActualNativeLookupWithoutInventedNormalization()
    {
        const string privateName = "Résumé.txt";
        var aliasName = privateName.Normalize(System.Text.NormalizationForm.FormD);
        var policy = new SafetySettings { AutoApprove = true,
            Blacklist = new() { ["private-spelling"] = new() { Path = "**/" + privateName, Intent = "read" } } };
        using var fixture = new Fixture(safety: policy);
        var source = Path.Combine(fixture.Session.WorkingDirectory!, privateName);
        var alias = Path.Combine(fixture.Session.WorkingDirectory!, aliasName);
        await File.WriteAllTextAsync(source, "private normalized filename", TestContext.Current.CancellationToken);
        var sameTarget = File.Exists(alias);
        if (!sameTarget) await File.WriteAllTextAsync(alias, "public distinct filename", TestContext.Current.CancellationToken);
        var result = await fixture.Execute("read", new JsonObject { ["file_path"] = alias });
        Assert.Equal(sameTarget, result.IsError);
        var content = string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.DoesNotContain("private normalized filename", content);
        if (!sameTarget) Assert.Contains("public distinct filename", content);
    }

    [Fact]
    public async Task UnsupportedUnicodeWildcardEquivalence_FailsBeforeReadWithoutInventingARuleMatch()
    {
        var policy = new SafetySettings { AutoApprove = true,
            Blacklist = new() { ["uncertain-fold"] = new() { Path = "**/SEC*.TXT", Intent = "read" } } };
        using var fixture = new Fixture(safety: policy);
        var target = Path.Combine(fixture.Session.WorkingDirectory!, "secrét.txt");
        await File.WriteAllTextAsync(target, "unresolved wildcard private content", TestContext.Current.CancellationToken);
        var result = await fixture.Execute("read", new JsonObject { ["file_path"] = target });
        Assert.True(result.IsError);
        Assert.DoesNotContain("unresolved wildcard private content", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.DoesNotContain(fixture.SafetyAudits, audit => audit.RuleId == "uncertain-fold");
        policy.Blacklist.Clear();
        var allowed = await fixture.Execute("read", new JsonObject { ["file_path"] = target });
        Assert.False(allowed.IsError);
        Assert.Contains("unresolved wildcard private content", string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
    }

    [Fact]
    public async Task UnrelatedGlobDeny_PermitsActualNewDirectoriesButNeverCreatesTheDeniedFile()
    {
        var policy = new SafetySettings { AutoApprove = true,
            Blacklist = new() { ["private-new-name"] = new() { Path = "**/secret.txt" } } };
        using var fixture = new Fixture(safety: policy);
        var directory = Path.Combine(fixture.Session.WorkingDirectory!, "fresh", "nested");
        var publicFile = Path.Combine(directory, "public.txt");
        var deniedFile = Path.Combine(directory, "secret.txt");
        Assert.False((await fixture.Execute("write", new JsonObject { ["file_path"] = publicFile, ["content"] = "new public content" })).IsError);
        Assert.Equal("new public content", await File.ReadAllTextAsync(publicFile, TestContext.Current.CancellationToken));
        Assert.True((await fixture.Execute("write", new JsonObject { ["file_path"] = deniedFile, ["content"] = "must not publish" })).IsError);
        Assert.False(File.Exists(deniedFile));
    }

    [Fact]
    public async Task AutomaticApproval_DoesNotResolveAnIrrelevantWhitelistSymlinkCycle()
    {
        var policy = new SafetySettings { AutoApprove = true };
        using var fixture = new Fixture(safety: policy);
        var directory = fixture.Session.WorkingDirectory!;
        var first = Path.Combine(directory, "loop-first");
        var second = Path.Combine(directory, "loop-second");
        try
        {
            Directory.CreateSymbolicLink(first, second);
            Directory.CreateSymbolicLink(second, first);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        { Assert.Skip("The account lacks symbolic-link creation authority."); }
        policy.Whitelist["unused-cycle"] = new() { Path = Path.Combine(first, "**"), Intent = "read" };
        var ordinary = Path.Combine(directory, "ordinary.txt");
        await File.WriteAllTextAsync(ordinary, "automatically allowed content", TestContext.Current.CancellationToken);
        var allowed = await fixture.Execute("read", new JsonObject { ["file_path"] = ordinary });
        Assert.False(allowed.IsError);
        Assert.Contains("automatically allowed content", string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
        policy.Blacklist["still-denied"] = new() { Path = ordinary, Intent = "read" };
        Assert.True((await fixture.Execute("read", new JsonObject { ["file_path"] = ordinary })).IsError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonFileApproval_RechecksTheCurrentPolicyBeforeAnyHttpRequest(bool explicitSessionDenial)
    {
        var policy = new SafetySettings();
        using var fixture = new Fixture(safety: policy);
        await using var origin = await PluginSdkHttpServer.StartAsync();
        using var client = new HttpClient();
        using (var control = await client.PostAsync(origin.Url, new StringContent("control"), TestContext.Current.CancellationToken))
            Assert.Equal(System.Net.HttpStatusCode.Created, control.StatusCode);
        Assert.Equal(1, origin.Requests);
        using var registration = fixture.Tools.Register(new ToolDefinition
        {
            Name = "non_file_http", Description = "Perform one actual HTTP request after admission.",
            Parameters = ToolSchemas.Parse("{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}},\"required\":[\"url\"]}"),
            Output = new ToolOutputDefinition(ToolSchemas.Parse("{\"type\":\"object\"}"), (_, value) => [new TextBlock(value.GetRawText())]),
            Execute = async (arguments, run) =>
            {
                using var content = new StringContent("model operation");
                using var response = await client.PostAsync(arguments.GetProperty("url").GetString(), content, run.Signal);
                return new JsonObject { ["status"] = (int)response.StatusCode };
            },
        });
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribe = fixture.Context.OnWaterfall<ApprovalRequestNotification>((_, _) =>
        { asked.TrySetResult(); return new(answer.Task); }, new EventOptions { Global = true });
        try
        {
            var pending = fixture.Execute("non_file_http", new JsonObject { ["url"] = origin.Url });
            await asked.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (explicitSessionDenial) ApprovalService.SetApprovalPolicy(fixture.Session, ApprovalPolicy.Never);
            else policy.Blacklist["current-url-deny"] = new() { Args = new() { ["url"] = origin.Url } };
            answer.SetResult(ApprovalOutcome.AllowedOnce);
            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(result.IsError);
            Assert.Equal(1, origin.Requests);
            if (explicitSessionDenial) Assert.Contains(fixture.SafetyAudits, audit => audit.Outcome == "rejected");
            else Assert.Contains(fixture.SafetyAudits, audit => audit.RuleId == "current-url-deny" && audit.Decision == "deny");
        }
        finally { answer.TrySetResult(ApprovalOutcome.Cancelled); unsubscribe(); }
    }

    [Fact(Timeout = 60000)]
    public async Task DirectoryView_MovedIntoDeniedTreeDuringEntryApprovalNeverReturnsTheOldSnapshot()
    {
        var policy = new SafetySettings();
        using var fixture = new Fixture(safety: policy);
        var source = Path.Combine(fixture.Session.WorkingDirectory!, "public-directory");
        var protectedRoot = Path.Combine(fixture.Session.WorkingDirectory!, "protected-directory");
        var moved = Path.Combine(protectedRoot, "moved");
        const string entry = "private-entry-name.txt";
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(protectedRoot);
        await File.WriteAllTextAsync(Path.Combine(source, entry), "directory entry content", TestContext.Current.CancellationToken);
        policy.Whitelist["public-list-root"] = new() { Path = source, Intent = "read" };
        policy.Blacklist["private-list-tree"] = new() { Path = Path.Combine(protectedRoot, "**"), Intent = "read" };
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribe = fixture.Context.OnWaterfall<ApprovalRequestNotification>((_, _) =>
        { asked.TrySetResult(); return new(answer.Task); }, new EventOptions { Global = true });
        try
        {
            var pending = fixture.Execute("str_replace_editor", new JsonObject { ["command"] = "view", ["path"] = source });
            await asked.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (OperatingSystem.IsWindows())
                Assert.Throws<IOException>(() => Directory.Move(source, moved));
            else
            {
                Directory.Move(source, moved);
                Assert.False(Directory.Exists(source));
                Assert.True(File.Exists(Path.Combine(moved, entry)));
            }
            answer.SetResult(ApprovalOutcome.AllowedOnce);
            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);
            var content = string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));
            if (OperatingSystem.IsWindows())
            {
                Assert.False(result.IsError);
                Assert.Contains(entry, content);
            }
            else
            {
                Assert.True(result.IsError);
                Assert.DoesNotContain(entry, content);
            }
        }
        finally { answer.TrySetResult(ApprovalOutcome.Cancelled); unsubscribe(); }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("read_image")]
    [InlineData("glob")]
    [InlineData("grep")]
    [InlineData("str_replace_editor")]
    public async Task ActualReadTargets_AreDeniedBeforeContentOrDirectoryResultsEscape(string tool)
    {
        var policy = new SafetySettings { AutoApprove = true, Blacklist = new()
            { ["private-source"] = new() { Path = "**/secret.png", Intent = "read" } } };
        using var fixture = new Fixture(safety: policy);
        var target = Path.Combine(fixture.Session.WorkingDirectory!, "secret.png");
        await File.WriteAllTextAsync(target, "private-content-not-for-model", TestContext.Current.CancellationToken);
        var args = tool switch
        {
            "glob" => new JsonObject { ["path"] = fixture.Session.WorkingDirectory, ["pattern"] = "**/*" },
            "grep" => new JsonObject { ["path"] = fixture.Session.WorkingDirectory, ["pattern"] = "private-content" },
            "str_replace_editor" => new JsonObject { ["path"] = target, ["command"] = "view" },
            _ => new JsonObject { ["file_path"] = target },
        };
        var result = await fixture.Execute(tool, args);
        Assert.True(result.IsError);
        Assert.DoesNotContain("private-content-not-for-model", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Contains(fixture.SafetyAudits, audit => audit.RuleId == "private-source" && audit.Decision == "deny"
            && audit.Intent == "read" && audit.Path == Dsh.Transport.PinnedFileAccess.Canonicalize(target));
        Assert.Empty(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualAliases_CannotBypassAFileDeny(bool hardlink)
    {
        var policy = new SafetySettings { AutoApprove = true };
        using var fixture = new Fixture(safety: policy);
        var target = Path.Combine(fixture.Session.WorkingDirectory!, "original.txt");
        var alias = Path.Combine(fixture.Session.WorkingDirectory!, "alias.txt");
        await File.WriteAllTextAsync(target, "original is private", TestContext.Current.CancellationToken);
        if (hardlink)
        {
            if (OperatingSystem.IsWindows()) Assert.True(CreateHardLinkWindows(alias, target, IntPtr.Zero));
            else Assert.Equal(0, CreateHardLinkUnix(target, alias));
        }
        else
        {
            try { File.CreateSymbolicLink(alias, target); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            { Assert.Skip("The account lacks symbolic-link creation authority."); }
        }
        policy.Blacklist["deny-original"] = new() { Path = target, Intent = "write" };
        var result = await fixture.Execute("write", new JsonObject { ["file_path"] = alias, ["content"] = "not committed" });
        Assert.True(result.IsError);
        Assert.Equal("original is private", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal("original is private", await File.ReadAllTextAsync(alias, TestContext.Current.CancellationToken));
        Assert.Contains(fixture.SafetyAudits, audit => audit.RuleId == "deny-original" && audit.Decision == "deny");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalForOneRead_DoesNotAuthorizeAnotherPathOrWrite(bool anotherPath)
    {
        using var fixture = new Fixture(safety: new SafetySettings());
        var first = Path.Combine(fixture.Session.WorkingDirectory!, "first.txt");
        var second = Path.Combine(fixture.Session.WorkingDirectory!, "second.txt");
        await File.WriteAllTextAsync(first, "first original", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "second original", TestContext.Current.CancellationToken);
        using var registration = fixture.Tools.Register(new ToolDefinition
        {
            Name = "two_operations", Description = "Perform two actual independently authorized operations.",
            MediatesFileAccess = true, FileIntent = _ => "read",
            Parameters = ToolSchemas.Parse("{\"type\":\"object\"}"),
            Output = new ToolOutputDefinition(ToolSchemas.Parse("{\"type\":\"null\"}"), (_, _) => []),
            Execute = async (_, run) =>
            {
                var original = await SettingsMutationScope.ReadTextAsync(first, run);
                if (anotherPath) await SettingsMutationScope.ReadTextAsync(second, run);
                else await SettingsMutationScope.WriteTextAsync(first, original, "unauthorized replacement", run);
                return null;
            },
        });
        var count = 0;
        var unsubscribe = fixture.Context.OnWaterfall<ApprovalRequestNotification>((_, _) =>
            ValueTask.FromResult<object?>(Interlocked.Increment(ref count) == 1 ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected),
            new EventOptions { Global = true });
        try
        {
            var result = await fixture.Execute("two_operations", new JsonObject { ["file_path"] = first });
            Assert.True(result.IsError);
            Assert.Equal(2, count);
            Assert.Equal("first original", await File.ReadAllTextAsync(first, TestContext.Current.CancellationToken));
            Assert.Equal("second original", await File.ReadAllTextAsync(second, TestContext.Current.CancellationToken));
        }
        finally { unsubscribe(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyChangeDuringRealApproval_IsRecheckedBeforeOpeningTheTarget(bool explicitSessionDenial)
    {
        var policy = new SafetySettings();
        using var fixture = new Fixture(safety: policy);
        var target = Path.Combine(fixture.Session.WorkingDirectory!, "pending.txt");
        await File.WriteAllTextAsync(target, "do not disclose", TestContext.Current.CancellationToken);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribe = fixture.Context.OnWaterfall<ApprovalRequestNotification>((_, _) =>
        { asked.TrySetResult(); return new(answer.Task); }, new EventOptions { Global = true });
        try
        {
            var pending = fixture.Execute("read", new JsonObject { ["file_path"] = target });
            await asked.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (explicitSessionDenial) ApprovalService.SetApprovalPolicy(fixture.Session, ApprovalPolicy.Never);
            else policy.Blacklist["new-deny"] = new() { Path = target, Intent = "read" };
            answer.SetResult(ApprovalOutcome.AllowedOnce);
            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(result.IsError);
            Assert.DoesNotContain("do not disclose", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
            if (explicitSessionDenial)
                Assert.Contains(fixture.SafetyAudits, audit => audit.Decision == "notify" && audit.Outcome == "rejected");
            else
                Assert.Contains(fixture.SafetyAudits, audit => audit.RuleId == "new-deny" && audit.Decision == "deny");
        }
        finally { answer.TrySetResult(ApprovalOutcome.Cancelled); unsubscribe(); }
    }

    [Fact]
    public async Task RootedRecursiveGlob_DeniesAnActualReadBelowTheFilesystemRoot()
    {
        var policy = new SafetySettings { AutoApprove = true };
        using var fixture = new Fixture(safety: policy);
        var target = Path.Combine(fixture.Session.WorkingDirectory!, "rooted-canary.txt");
        await File.WriteAllTextAsync(target, "rooted glob canary", TestContext.Current.CancellationToken);
        Assert.False((await fixture.Execute("read", new JsonObject { ["file_path"] = target })).IsError);
        policy.Blacklist["rooted-glob"] = new()
        {
            Tool = ["read"], Intent = "read",
            Path = Path.Combine(Path.GetPathRoot(target)!, "**", Path.GetFileName(target)),
        };
        var result = await fixture.Execute("read", new JsonObject { ["file_path"] = target });
        Assert.True(result.IsError);
        Assert.DoesNotContain("rooted glob canary", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Contains(fixture.SafetyAudits, audit => audit.RuleId == "rooted-glob" && audit.Decision == "deny");
    }

    [Fact]
    public async Task UnixBackslashFileName_IsNotTreatedAsADirectoryAlias()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Backslash is a directory separator on Windows.");
        var policy = new SafetySettings { AutoApprove = true };
        using var fixture = new Fixture(safety: policy);
        var target = Path.Combine(fixture.Session.WorkingDirectory!, "odd\\name.txt");
        await File.WriteAllTextAsync(target, "ordinary backslash filename", TestContext.Current.CancellationToken);
        policy.Blacklist["different-directory"] = new() { Path = "odd/name.txt", Intent = "read" };
        Assert.False((await fixture.Execute("read", new JsonObject { ["file_path"] = target })).IsError);
        policy.Blacklist["literal-backslash"] = new() { Path = target, Intent = "read" };
        Assert.True((await fixture.Execute("read", new JsonObject { ["file_path"] = target })).IsError);
    }

    [Fact]
    public async Task SafetyAudit_RoundTripsAsIgnorableMetadataWithTheRealDecision()
    {
        using var fixture = new Fixture(safety: new SafetySettings { AutoApprove = true,
            Blacklist = new() { ["deny-read"] = new() { Tool = ["read"] } } });
        Assert.True((await fixture.Execute("read", new JsonObject { ["file_path"] = "not-opened.txt" })).IsError);
        var item = Assert.Single(fixture.Session.SnapshotEvents(), entry => entry.Data is SafetyDecisionAuditPayload { RuleId: "deny-read" });
        var encoded = DshJson.Serialize(item);
        var decoded = DshJson.Deserialize<SessionEvent>(encoded)!;
        Assert.True(decoded.Ignorable);
        var audit = Assert.IsType<SafetyDecisionAuditPayload>(decoded.Data);
        Assert.Equal("deny", audit.Decision);
        Assert.Equal("blacklist", audit.RuleKind);
        Assert.Equal("read", audit.Tool);
        Assert.Equal(fixture.Session.Id.Value, audit.SessionId);
        Assert.NotNull(audit.CallId);
    }

    [Fact]
    public async Task SuccessfulRead_PostInstructionsCannotInjectDeniedFile_AndKeepsTheAllowedContent()
    {
        var policy = new SafetySettings { Whitelist = new() { ["read-invocation"] = new() { Tool = ["read"] } } };
        using var fixture = new Fixture(safety: policy);
        var cwd = fixture.Session.WorkingDirectory!;
        fixture.Context.SetOwn("harnessOptions", new HarnessOptions(fixture.Home, Cwd: cwd));
        using var instructions = new Dsh.AgentInstructions.Plugin("@deepseek-ai/dsh-agent-instructions").Apply(fixture.Context, null);
        var allowedDirectory = Directory.CreateDirectory(Path.Combine(cwd, "allowed-instructions")).FullName;
        var allowedPath = Path.Combine(allowedDirectory, "public.txt");
        await File.WriteAllTextAsync(allowedPath, "allowed-source-content", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(allowedDirectory, "AGENTS.md"), "allowed-instruction-marker", TestContext.Current.CancellationToken);
        var allowed = await fixture.Execute("read", new JsonObject { ["file_path"] = allowedPath });
        Assert.False(allowed.IsError, string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Contains("allowed-instruction-marker", string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));

        var deniedDirectory = Directory.CreateDirectory(Path.Combine(cwd, "denied-instructions")).FullName;
        var source = Path.Combine(deniedDirectory, "public.txt");
        var privateInstructions = Path.Combine(deniedDirectory, "AGENTS.md");
        await File.WriteAllTextAsync(source, "second-allowed-source-content", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(privateInstructions, "private-post-read-instruction-marker", TestContext.Current.CancellationToken);
        policy.Blacklist["deny-post-instructions"] = new() { Path = privateInstructions, Intent = "read" };
        var callId = ToolCallId.Create(Guid.NewGuid().ToString("N"));
        var result = await fixture.Tools.Execute(new ToolExecutionInput
        {
            CallId = callId, Name = "read", Agent = fixture.Agent,
            Arguments = JsonSerializer.SerializeToElement(new { file_path = source }), Signal = TestContext.Current.CancellationToken,
        });
        Assert.False(result.IsError, string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        var content = string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.Contains("second-allowed-source-content", content);
        Assert.DoesNotContain("private-post-read-instruction-marker", content);
        Assert.DoesNotContain("private-post-read-instruction-marker", JsonSerializer.Serialize(result.AdditionalContexts));
        Assert.Contains(fixture.SafetyAudits, audit => audit.RuleId == "deny-post-instructions" && audit.Decision == "deny"
            && audit.Path == Dsh.Transport.PinnedFileAccess.Canonicalize(privateInstructions) && audit.Intent == "read"
            && audit.Tool == "read" && audit.SessionId == fixture.Session.Id.Value && audit.CallId == callId.Value);
    }

    [Theory]
    [InlineData("delete-file")]
    [InlineData("replace-directory")]
    [InlineData("cancel")]
    public async Task SuccessfulRead_OptionalInstructionsRemovedDuringApproval_DoNotLoseAllowedReadOrSwallowCancellation(string change)
    {
        var policy = new SafetySettings { AutoApprove = false };
        using var fixture = new Fixture(safety: policy);
        var cwd = fixture.Session.WorkingDirectory!;
        fixture.Context.SetOwn("harnessOptions", new HarnessOptions(fixture.Home, Cwd: cwd));
        using var instructions = new Dsh.AgentInstructions.Plugin("@deepseek-ai/dsh-agent-instructions").Apply(fixture.Context, null);
        var directory = Directory.CreateDirectory(Path.Combine(cwd, "optional-instructions")).FullName;
        var source = Path.Combine(directory, "public.txt");
        var optional = Path.Combine(directory, "AGENTS.md");
        await File.WriteAllTextAsync(source, "allowed-source-during-optional-removal", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(optional, "private-optional-instruction-body", TestContext.Current.CancellationToken);
        var auditPath = Dsh.Transport.PinnedFileAccess.Canonicalize(optional);
        policy.Whitelist["only-primary-source"] = new() { Tool = ["read"], Path = source, Intent = "read" };
        var asked = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remove = fixture.Context.OnWaterfall<ApprovalRequestNotification>((notification, _) =>
        {
            asked.TrySetResult(notification.Request);
            return new(answer.Task);
        }, new EventOptions { Global = true });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var callId = ToolCallId.Create(Guid.NewGuid().ToString("N"));
        try
        {
            var pending = fixture.Tools.Execute(new ToolExecutionInput
            {
                CallId = callId, Name = "read", Agent = fixture.Agent,
                Arguments = JsonSerializer.SerializeToElement(new { file_path = source }), Signal = cancellation.Token,
            });
            var first = await Task.WhenAny(asked.Task, pending).WaitAsync(TestContext.Current.CancellationToken);
            if (first == pending)
                Assert.Fail($"Read completed before requesting the optional instruction approval: {string.Concat((await pending).Content.OfType<TextBlock>().Select(block => block.Text))}");
            await asked.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (change == "replace-directory")
            {
                Directory.Move(directory, directory + ".retired");
                await File.WriteAllTextAsync(directory, "directory replaced during optional approval", TestContext.Current.CancellationToken);
            }
            else
                File.Delete(optional);
            if (change == "cancel") cancellation.Cancel();
            answer.TrySetResult(ApprovalOutcome.AllowedOnce);

            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);
            var content = string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));
            Assert.Equal(change == "cancel", result.IsError);
            if (change != "cancel") Assert.Contains("allowed-source-during-optional-removal", content);
            Assert.DoesNotContain("private-optional-instruction-body", content);
            Assert.DoesNotContain("private-optional-instruction-body", JsonSerializer.Serialize(result.AdditionalContexts));
            Assert.Contains(fixture.SafetyAudits, audit => audit.Path == auditPath && audit.Intent == "read"
                && audit.Decision == "notify" && audit.Tool == "read"
                && audit.SessionId == fixture.Session.Id.Value && audit.CallId == callId.Value);
        }
        finally
        {
            answer.TrySetResult(ApprovalOutcome.Cancelled);
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
        public IDisposable Reviewer { get; }
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
            if (safety is not null) _ = new SafetyBoundary(Context, () => new HarnessSettings { Safety = safety });
            var boundary = new SettingsMutationBoundary(Context, Home);
            Reviewer = boundary.InstallReviewer(new SettingsMutationGate(Context, Home));
            _tools = [WriteTool.Register(Context), EditTool.Register(Context), StrReplaceEditorTool.Register(Context),
                ReadTool.Register(Context), ReadImageTool.Register(Context), GlobTool.Register(Context), GrepTool.Register(Context)];
            _tools.Add(Tools.Register(new ToolDefinition
            {
                Name = "typed_settings_update", Description = "Update a real settings section through the typed writer.",
                MediatesFileAccess = true,
                FileIntent = _ => "write",
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
            Reviewer.Dispose();
            Agent.Dispose();
            TempTree.Delete(Root);
        }
    }
}
