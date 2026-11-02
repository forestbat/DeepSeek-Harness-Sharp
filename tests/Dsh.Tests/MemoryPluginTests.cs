using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Memory;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class MemoryPluginTests
{
    [Fact]
    public void Factory_UsesFileBackendByDefault()
    {
        var store = MemoryStoreFactory.Create(MemoryPluginConfig.Resolve(null), Path.GetTempPath());

        var file = Assert.IsType<FileMemoryStore>(store);
        Assert.EndsWith(".dsh-memory.md", file.Description);
    }

    [Fact]
    public void Factory_UsesMongoBackendWhenConfigured()
    {
        var store = MemoryStoreFactory.Create(MemoryPluginConfig.Resolve(new Dictionary<string, object?>
        {
            ["backend"] = "mongo",
            ["mongo"] = new Dictionary<string, object?> { ["database"] = "dsh", ["collection"] = "memory", ["key"] = "k1" },
        }), Path.GetTempPath());

        var mongo = Assert.IsType<MongoTextStore>(store);
        Assert.Equal("dsh.memory#k1", mongo.Description);
        mongo.Dispose();
    }

    [Fact]
    public async Task FileStore_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsh-memory-{Guid.NewGuid():N}", "memory.md");
        try
        {
            var store = new FileMemoryStore(path);
            Assert.Null(await store.GetAsync(TestContext.Current.CancellationToken));

            await store.SetAsync("# Memory\n- fact", TestContext.Current.CancellationToken);

            Assert.Equal("# Memory\n- fact", await store.GetAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public async Task SaveTool_RememberCorrectForgetSkip()
    {
        using var fixture = new ToolFixture();

        var remembered = await fixture.Execute("""{"action":"remember","key":"build.command","text":"dotnet build","section":"Commands"}""");
        Assert.IsType<ToolExecutionResult.Success>(remembered);
        var text = File.ReadAllText(fixture.MemoryPath);
        Assert.Contains("## Commands", text);
        Assert.Contains("- build.command :: dotnet build (", text);

        var corrected = await fixture.Execute("""{"action":"correct","key":"no.force.push","text":"never force push"}""");
        Assert.IsType<ToolExecutionResult.Success>(corrected);
        Assert.Contains("## Corrections", File.ReadAllText(fixture.MemoryPath));

        var forgotten = await fixture.Execute("""{"action":"forget","key":"build.command"}""");
        Assert.IsType<ToolExecutionResult.Success>(forgotten);
        Assert.DoesNotContain("build.command", File.ReadAllText(fixture.MemoryPath));

        var before = File.ReadAllText(fixture.MemoryPath);
        var skipped = await fixture.Execute("""{"action":"skip","reason":"user preference"}""");
        Assert.IsType<ToolExecutionResult.Success>(skipped);
        Assert.Equal(before, File.ReadAllText(fixture.MemoryPath));
    }

    [Fact]
    public async Task SaveTool_RequiresTextForRemember()
    {
        using var fixture = new ToolFixture();

        var result = await fixture.Execute("""{"action":"remember","key":"a"}""");

        Assert.IsType<ToolExecutionResult.Failure>(result);
    }

    [Fact]
    public async Task SaveTool_ForgetByKeysAndDryRun()
    {
        using var fixture = new ToolFixture();
        _ = await fixture.Execute("""{"action":"remember","key":"a","text":"alpha dotnet","section":"Facts"}""");
        _ = await fixture.Execute("""{"action":"remember","key":"b","text":"beta dotnet","section":"Commands"}""");

        var preview = await fixture.Execute("""{"action":"forget","query":"dotnet","dry_run":true}""");
        Assert.IsType<ToolExecutionResult.Success>(preview);
        Assert.Contains("dotnet", File.ReadAllText(fixture.MemoryPath));

        var removed = await fixture.Execute("""{"action":"forget","keys":["a","b"]}""");
        Assert.IsType<ToolExecutionResult.Success>(removed);
        var text = File.ReadAllText(fixture.MemoryPath);
        Assert.DoesNotContain("alpha dotnet", text);
        Assert.DoesNotContain("beta dotnet", text);
    }

    [Fact]
    public async Task Workspace_ResolvesPerProjectRootAndCaches()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-memory-ws-{Guid.NewGuid():N}");
        var projectA = Path.Combine(root, "a");
        var projectB = Path.Combine(root, "b");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);
        try
        {
            using var workspace = new MemoryWorkspace(MemoryPluginConfig.Resolve(null));
            var memoryA = workspace.For(projectA);
            Assert.Same(memoryA, workspace.For(projectA));
            var memoryB = workspace.For(projectB);
            Assert.NotSame(memoryA, memoryB);

            await memoryA.RememberAsync("key", "alpha", "Facts", "test", TestContext.Current.CancellationToken);

            Assert.Contains("alpha", await File.ReadAllTextAsync(Path.Combine(projectA, MemoryStoreFactory.DefaultFileName), TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(projectB, MemoryStoreFactory.DefaultFileName)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("audit")]
    public async Task SaveTool_ActualFileRulesDenyReadWriteAndAudit_WhileUnscopedStorageRemainsTrusted(string operation)
    {
        var policy = new SafetySettings { Whitelist = new() { ["memory-invocation"] = new() { Tool = ["memory_save"] } } };
        using var fixture = new ToolFixture(policy);
        var allowed = await fixture.Execute("""{"action":"remember","key":"private-first-key","text":"private-first-value"}""");
        Assert.False(allowed.IsError, string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
        var store = new FileMemoryStore(fixture.MemoryPath);
        Assert.Contains("private-first-value", await store.GetAsync(TestContext.Current.CancellationToken));
        var before = File.ReadAllText(fixture.MemoryPath);
        var auditBefore = File.ReadAllText(fixture.AuditPath);
        var target = operation == "audit" ? fixture.AuditPath : fixture.MemoryPath;
        var intent = operation == "read" ? "read" : "write";
        var ruleId = $"deny-memory-{operation}";
        policy.Blacklist[ruleId] = new() { Path = target, Intent = intent };

        var denied = await fixture.Execute(operation == "read"
            ? """{"action":"forget","query":"private-first-value","dry_run":true}"""
            : """{"action":"remember","key":"approved-second-key","text":"approved-second-value"}""");
        Assert.True(denied.IsError);
        Assert.DoesNotContain("private-first-key", string.Concat(denied.Content.OfType<TextBlock>().Select(block => block.Text)));
        if (operation == "audit")
        {
            var committed = File.ReadAllText(fixture.MemoryPath);
            if (!committed.Contains("approved-second-value", StringComparison.Ordinal))
                Assert.Fail("The expected memory update was not committed before the audit rejection.\n"
                    + string.Concat(denied.Content.OfType<TextBlock>().Select(block => block.Text)) + "\n"
                    + string.Join("\n", fixture.Session!.SnapshotEvents().Select(item => item.Data).OfType<SafetyDecisionAuditPayload>()
                        .Where(audit => audit.CallId == fixture.LastCallId.Value).Select(audit => audit.ToString())));
        }
        else
            Assert.Equal(before, File.ReadAllText(fixture.MemoryPath));
        Assert.Equal(auditBefore, File.ReadAllText(fixture.AuditPath));
        var session = Assert.IsType<Session>(fixture.Session);
        Assert.Contains(session.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload audit
            && audit.RuleId == ruleId && audit.RuleKind == "blacklist" && audit.Decision == "deny"
            && audit.Tool == "memory_save" && audit.SessionId == session.Id.Value && audit.CallId == fixture.LastCallId.Value
            && audit.Path == Dsh.Transport.PinnedFileAccess.Canonicalize(target) && audit.Intent == intent && audit.MatchedField == "path");

        Assert.Null(SettingsMutationScope.Current);
        var decisionCount = session.SnapshotEvents().Count(item => item.Data is SafetyDecisionAuditPayload);
        await fixture.Memory.RememberAsync("manual", "manual control-plane value", "Facts", "command", TestContext.Current.CancellationToken);
        Assert.Contains("manual control-plane value", await store.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(decisionCount, session.SnapshotEvents().Count(item => item.Data is SafetyDecisionAuditPayload));
        Assert.Null(SettingsMutationScope.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveTool_NonFileDryRun_AuthorizesBeforeBackendAccess(bool approved)
    {
        var store = new NonFileMemoryAuthorizationProbe();
        using var fixture = new ToolFixture(new SafetySettings { AutoApprove = false }, store);
        _ = ApprovalService.Register(fixture.Context);
        await fixture.Memory.RememberAsync("private-remote-key", "private-remote-value", "Facts", "command", TestContext.Current.CancellationToken);
        var original = store.Text;
        store.ResetObservations();
        store.HasCallApproval = () => fixture.Session!.SnapshotEvents().Any(item => item.Data is SafetyDecisionAuditPayload audit
            && audit.Tool == "memory_save" && audit.CallId == fixture.LastCallId.Value && audit.Outcome == "approved");
        using var answerer = approved ? ApprovalAnswerers.AutoApprove(fixture.Context) : ApprovalAnswerers.DenyAll(fixture.Context);

        var result = await fixture.Execute("""{"action":"forget","query":"private-remote-value","dry_run":true}""");

        var content = string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.Equal(!approved, result.IsError);
        Assert.Equal(original, store.Text);
        Assert.Equal(0, store.Writes);
        Assert.False(store.ReadBeforeApproval);
        if (approved)
        {
            Assert.True(store.Reads > 0);
            Assert.Contains("private-remote-key", content);
        }
        else
        {
            Assert.Equal(0, store.Reads);
            Assert.DoesNotContain("private-remote-key", content);
        }
        Assert.Contains(fixture.Session!.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload audit
            && audit.Decision == "notify" && audit.Outcome == (approved ? "approved" : "rejected")
            && audit.Tool == "memory_save" && audit.SessionId == fixture.Session.Id.Value && audit.CallId == fixture.LastCallId.Value);
    }

    // This backend is an authorization-order probe, not a MongoDB service substitute.
    private sealed class NonFileMemoryAuthorizationProbe : IMemoryStore
    {
        public string Description => "non-file authorization probe";
        public string? Text { get; private set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool ReadBeforeApproval { get; private set; }
        public Func<bool>? HasCallApproval { get; set; }

        public Task<string?> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            ReadBeforeApproval |= HasCallApproval?.Invoke() == false;
            return Task.FromResult(Text);
        }

        public Task SetAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            Text = text;
            return Task.CompletedTask;
        }

        public void ResetObservations()
        {
            Reads = 0;
            Writes = 0;
            ReadBeforeApproval = false;
        }
    }

    private sealed class ToolFixture : IDisposable
    {
        private readonly string _root;
        private readonly ToolRuntime _tools;
        private readonly IDisposable _tool;
        private readonly AgentLoopAgent? _agent;
        private readonly IDisposable? _settingsReviewer;

        public ToolFixture(SafetySettings? safety = null, IMemoryStore? store = null)
        {
            _root = TempTree.CreateDirectory("memory-tool");
            Directory.CreateDirectory(_root);
            MemoryPath = Path.Combine(_root, "memory.md");
            var ctx = new Context();
            Context = ctx;
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            _tools = new ToolRuntime(ctx);
            Memory = new ProjectMemory(store ?? new FileMemoryStore(MemoryPath), Path.Combine(_root, ".dsh-memory"));
            if (safety is not null)
            {
                _ = new LlmRuntime(ctx);
                _ = new SafetyBoundary(ctx, () => new HarnessSettings { Safety = safety });
                if (store is not null)
                {
                    var home = HarnessHome.Resolve(Path.Combine(_root, "home"));
                    Directory.CreateDirectory(home.Root);
                    File.WriteAllText(home.SettingsFile, "safety:\n  autoApprove: false\n");
                    var settings = HarnessSettings.Load(home);
                    settings.Safety = safety;
                    settings.Save(home);
                    var boundary = new SettingsMutationBoundary(ctx, home);
                    _settingsReviewer = boundary.InstallReviewer(new SettingsMutationGate(ctx, home));
                    _ = new ExternalProcessBoundary(ctx, home, settings);
                }
                var id = SessionId.Create(Guid.NewGuid().ToString("N"));
                var session = Session.Create(id, null, new SessionHeader
                {
                    Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = _root, CreatedAt = 0, IsSeeded = false,
                });
                session.Append(new TurnStartPayload(1));
                _agent = new AgentLoopAgent(ctx, id, new AgentOptions(), session, _ => 0);
            }
            _tool = MemorySaveTool.Register(ctx, _ => Memory);
        }

        public Context Context { get; }
        public string MemoryPath { get; }
        public string AuditPath => Path.Combine(_root, ".dsh-memory", "decisions.jsonl");
        public ProjectMemory Memory { get; }
        public Session? Session => _agent?.Session;
        public ToolCallId LastCallId { get; private set; }

        public Task<ToolExecutionResult> Execute(string arguments)
            => _tools.Execute(new ToolExecutionInput
            {
                CallId = LastCallId = ToolCallId.Create(Guid.NewGuid().ToString("N")),
                Name = "memory_save",
                Arguments = JsonDocument.Parse(arguments).RootElement,
                Agent = _agent,
                Signal = TestContext.Current.CancellationToken,
            });

        public void Dispose()
        {
            _tool.Dispose();
            _agent?.Dispose();
            _settingsReviewer?.Dispose();
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
    }
}
