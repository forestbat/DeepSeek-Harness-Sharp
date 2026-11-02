using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Skills;

namespace Dsh.Tests;

public class SkillTests
{
    [Fact]
    public async Task FileSystemProvider_Discovers_And_Loads_Directory_Skill()
    {
        var ctx = new Context();
        _ = new SkillRegistry(ctx, new SkillRegistryConfig()); var root = Path.Combine(Path.GetTempPath(), $"dsh-skills-{Guid.NewGuid():N}");
        try
        {
            var skillDir = Path.Combine(root, "alpha-skill");
            Directory.CreateDirectory(skillDir);
            await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), """
                ---
                name: alpha-skill
                description: Test skill
                ---

                # Alpha

                Follow the alpha protocol.
                """, TestContext.Current.CancellationToken);
            using var registration = SkillFilesystem.Apply(ctx, new SkillFilesystemConfig
            {
                ProviderName = "test-fs",
                IncludeDefaultRoots = false,
                CustomSkillDirs = [root],
                Watch = false,
            });

            var skills = ctx.Get<SkillRegistry>(SkillRegistry.ServiceName)!;
            var snapshot = await skills.Snapshot(new SkillViewOptions { Cwd = root });
            var summary = Assert.Single(snapshot.Skills);
            Assert.Equal("alpha-skill", summary.Name);
            Assert.Equal("Test skill", summary.Description);

            var definition = await skills.Get("alpha-skill", new SkillViewOptions { Cwd = root });
            Assert.NotNull(definition);
            Assert.Contains("Follow the alpha protocol", definition.Content);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /** /skill:NAME 提示词: 缺提示词报错; skill 不存在也照发提示词; 成功时把 skill 内容 + 提示词交给下一步。 */
    [Fact]
    public async Task SkillCommand_Takes_Skill_Name_And_Prompt()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var commands = CommandsService.Register(ctx);
        var skills = new SkillRegistry(ctx, new SkillRegistryConfig());
        using var registration = skills.Register(new SkillRegistration
        {
            Name = "demo-skill",
            Description = "demo",
            Source = "test",
            Content = "demo skill instructions",
        });
        _ = SkillCommand.Register(ctx);

        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("demo-provider", "demo-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();

        var missing = await commands.Execute(agent, "/skill:demo-skill", TestContext.Current.CancellationToken);
        Assert.NotNull(missing);
        var missingError = Assert.IsType<CommandResult.Error>(missing.Result);
        Assert.Contains("prompt is required", missingError.Text);
        Assert.Null(missingError.FollowupPrompt);

        var unknown = await commands.Execute(agent, "/skill:nope 帮我重构", TestContext.Current.CancellationToken);
        Assert.NotNull(unknown);
        var unknownError = Assert.IsType<CommandResult.Error>(unknown.Result);
        Assert.Equal("skill \"nope\" not found", unknownError.Text);
        Assert.Equal("帮我重构", unknownError.FollowupPrompt);

        var ok = await commands.Execute(agent, "/skill:demo-skill 帮我重构", TestContext.Current.CancellationToken);
        Assert.NotNull(ok);
        var success = Assert.IsType<CommandResult.Success>(ok.Result);
        Assert.Contains("""<skill_content name="demo-skill">""", success.FollowupPrompt);
        Assert.Contains("demo skill instructions", success.FollowupPrompt);
        Assert.EndsWith("帮我重构", success.FollowupPrompt);
    }

    [Fact]
    public void Plugin_Activates_Without_Resolving_Its_Own_Registry()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        _ = CommandsService.Register(ctx);

        using var registration = new Dsh.Skills.Plugin("@deepseek-ai/dsh-skill").Apply(ctx, null);

        Assert.NotNull(tools.Get(SkillCatalog.ToolName, new ScopeKey()));
        Assert.NotNull(ctx.Get<SkillRegistry>(SkillRegistry.ServiceName));
    }

    [Fact]
    public async Task CatalogTool_Loads_Skill_And_Catalog_Renders()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var skills = new SkillRegistry(ctx, new SkillRegistryConfig());
        using var registration = skills.Register(new SkillRegistration
        {
            Name = "demo-skill",
            Description = "demo <skill>",
            Source = "test",
            Content = "demo skill instructions",
        });
        using var catalog = new SkillCatalog(ctx, skills);

        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("demo-provider", "demo-model")), TestContext.Current.CancellationToken);
        var agent = handle.Agent;

        Assert.Contains(tools.Schemas(agent.ScopeKey), schema => schema.Name == SkillCatalog.ToolName);
        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = SkillCatalog.ToolName,
            Arguments = System.Text.Json.JsonDocument.Parse("""{"name":"demo-skill"}""").RootElement,
            Agent = agent,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.False(result.IsError);
        Assert.Contains("demo skill instructions", Assert.IsType<TextBlock>(result.Content[0]).Text);

        var rendered = SkillCatalog.RenderCatalog(
            [new SkillSummary
            {
                Name = "demo-skill",
                Description = "demo <skill>",
                Source = "test",
                Provider = "test",
                Invocation = new SkillInvocationPolicy(true, true),
            }],
            update: false,
            incomplete: false);
        Assert.Contains("<available_skills>", rendered);
        Assert.Contains("&lt;skill&gt;", rendered);
    }

    [Fact]
    public async Task CatalogTool_RealFileReadObservesCurrentDenyWithoutLeakingContent_WhileManualLoadingStillWorks()
    {
        var root = TempTree.CreateDirectory("skill-policy");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "private-skill")).FullName;
            var path = Path.Combine(directory, "SKILL.md");
            await File.WriteAllTextAsync(path, "---\nname: private-skill\ndescription: File read policy consumer\n---\nprivate-skill-body-marker\n",
                TestContext.Current.CancellationToken);
            var context = new Context();
            _ = new SystemPrompt(context, new SystemPromptConfig());
            _ = new LlmRuntime(context);
            var tools = new ToolRuntime(context);
            var registry = new SkillRegistry(context, new SkillRegistryConfig());
            using var registration = SkillFilesystem.Apply(context, new SkillFilesystemConfig
            {
                ProviderName = "policy-filesystem", IncludeDefaultRoots = false, CustomSkillDirs = [root], Watch = false,
            });
            using var catalog = new SkillCatalog(context, registry);
            var policy = new SafetySettings { Whitelist = new() { ["skill-invocation"] = new() { Tool = [SkillCatalog.ToolName] } } };
            _ = new SafetyBoundary(context, () => new HarnessSettings { Safety = policy });
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            var session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = root, CreatedAt = 0, IsSeeded = false,
            });
            session.Append(new TurnStartPayload(1));
            using var agent = new AgentLoopAgent(context, id, new AgentOptions(), session, _ => 0);
            var input = new ToolExecutionInput
            {
                CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Name = SkillCatalog.ToolName,
                Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { name = "private-skill" }),
                Agent = agent, Signal = TestContext.Current.CancellationToken,
            };
            var allowed = await tools.Execute(input);
            Assert.False(allowed.IsError, string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.Contains("private-skill-body-marker", string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
            policy.Blacklist["deny-skill-file"] = new() { Path = path, Intent = "read" };
            var deniedCall = ToolCallId.Create(Guid.NewGuid().ToString("N"));
            var denied = await tools.Execute(new ToolExecutionInput
            {
                CallId = deniedCall, Name = input.Name, Arguments = input.Arguments, Agent = agent,
                Signal = TestContext.Current.CancellationToken,
            });
            Assert.True(denied.IsError);
            Assert.DoesNotContain("private-skill-body-marker", string.Concat(denied.Content.OfType<TextBlock>().Select(block => block.Text)));
            var deniedAudit = session.SnapshotEvents().Select(item => item.Data).OfType<SafetyDecisionAuditPayload>()
                .FirstOrDefault(audit => audit.RuleId == "deny-skill-file" && audit.Decision == "deny" && audit.Intent == "read"
                    && audit.Path == Dsh.Sandbox.SandboxFileAccess.Canonicalize(path)
                    && audit.Tool == SkillCatalog.ToolName && audit.SessionId == id.Value && audit.CallId == deniedCall.Value);
            if (deniedAudit is null) Assert.Fail(CallSafetyFailure(session, deniedCall, denied));
            var manual = await registry.Get("private-skill", new SkillViewOptions { Cwd = root, Signal = TestContext.Current.CancellationToken });
            Assert.NotNull(manual);
            Assert.Contains("private-skill-body-marker", manual.Content);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CatalogTool_NativeSkillLinks_AllowContentThenDenyItsRealTarget(bool directoryLink)
    {
        var root = TempTree.CreateDirectory("skill-link-policy");
        try
        {
            var skills = Directory.CreateDirectory(Path.Combine(root, "skills")).FullName;
            var storage = Directory.CreateDirectory(Path.Combine(root, "storage")).FullName;
            var target = directoryLink
                ? Path.Combine(Directory.CreateDirectory(Path.Combine(storage, "linked-skill")).FullName, "SKILL.md")
                : Path.Combine(storage, "linked-skill.md");
            await File.WriteAllTextAsync(target,
                "---\nname: linked-skill\ndescription: Native link consumer\n---\nlinked-private-body\n",
                TestContext.Current.CancellationToken);
            try
            {
                if (directoryLink)
                    Directory.CreateSymbolicLink(Path.Combine(skills, "linked-skill"), Path.GetDirectoryName(target)!);
                else
                    File.CreateSymbolicLink(Path.Combine(skills, "linked-skill.md"), target);
            }
            catch (Exception error) when (OperatingSystem.IsWindows()
                && error is IOException or UnauthorizedAccessException && (error.HResult & 0xffff) == 1314)
            {
                Assert.Skip("The test account lacks the Windows symbolic-link creation privilege.");
            }

            var context = new Context();
            _ = new SystemPrompt(context, new SystemPromptConfig());
            _ = new LlmRuntime(context);
            var tools = new ToolRuntime(context);
            var registry = new SkillRegistry(context, new SkillRegistryConfig());
            using var registration = SkillFilesystem.Apply(context, new SkillFilesystemConfig
            {
                ProviderName = "linked-filesystem", IncludeDefaultRoots = false, CustomSkillDirs = [skills], Watch = false,
            });
            using var catalog = new SkillCatalog(context, registry);
            var policy = new SafetySettings { Whitelist = new() { ["skill-call"] = new() { Tool = [SkillCatalog.ToolName] } } };
            _ = new SafetyBoundary(context, () => new HarnessSettings { Safety = policy });
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            var session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = root, CreatedAt = 0, IsSeeded = false,
            });
            session.Append(new TurnStartPayload(1));
            using var agent = new AgentLoopAgent(context, id, new AgentOptions(), session, _ => 0);
            ToolExecutionInput Input() => new()
            {
                CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Name = SkillCatalog.ToolName,
                Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { name = "linked-skill" }),
                Agent = agent, Signal = TestContext.Current.CancellationToken,
            };
            var manual = await registry.Get("linked-skill", new SkillViewOptions { Cwd = root, Signal = TestContext.Current.CancellationToken });
            Assert.NotNull(manual);
            Assert.Contains("linked-private-body", manual.Content);
            var allowed = await tools.Execute(Input());
            Assert.False(allowed.IsError, string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.Contains("linked-private-body", string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));

            policy.Blacklist["deny-linked-target"] = new() { Path = target, Intent = "read" };
            var deniedInput = Input();
            var denied = await tools.Execute(deniedInput);
            Assert.True(denied.IsError);
            Assert.DoesNotContain("linked-private-body", string.Concat(denied.Content.OfType<TextBlock>().Select(block => block.Text)));
            var deniedAudit = session.SnapshotEvents().Select(item => item.Data).OfType<SafetyDecisionAuditPayload>()
                .FirstOrDefault(audit => audit.RuleId == "deny-linked-target" && audit.Decision == "deny" && audit.Intent == "read"
                    && audit.Path == Dsh.Sandbox.SandboxFileAccess.Canonicalize(target)
                    && audit.Tool == SkillCatalog.ToolName && audit.SessionId == id.Value && audit.CallId == deniedInput.CallId.Value);
            if (deniedAudit is null) Assert.Fail(CallSafetyFailure(session, deniedInput.CallId, denied));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory]
    [InlineData("dangling.md")]
    [InlineData("dangling.bin")]
    public async Task CatalogTool_DanglingEntry_DoesNotHideAnExistingHealthySkill(string danglingName)
    {
        var root = TempTree.CreateDirectory("skill-dangling-entry");
        try
        {
            var healthy = Directory.CreateDirectory(Path.Combine(root, "healthy-skill")).FullName;
            await File.WriteAllTextAsync(Path.Combine(healthy, "SKILL.md"),
                "---\nname: healthy-skill\ndescription: Healthy sibling consumer\n---\nhealthy-sibling-body\n",
                TestContext.Current.CancellationToken);
            var context = new Context();
            _ = new SystemPrompt(context, new SystemPromptConfig());
            _ = new LlmRuntime(context);
            var tools = new ToolRuntime(context);
            var registry = new SkillRegistry(context, new SkillRegistryConfig());
            using var registration = SkillFilesystem.Apply(context, new SkillFilesystemConfig
            {
                ProviderName = "dangling-entry-filesystem", IncludeDefaultRoots = false, CustomSkillDirs = [root], Watch = false,
            });
            using var catalog = new SkillCatalog(context, registry);
            _ = new SafetyBoundary(context, () => new HarnessSettings
            {
                Safety = new() { Whitelist = new() { ["skill-call"] = new() { Tool = [SkillCatalog.ToolName] } } },
            });
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            var session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = root, CreatedAt = 0, IsSeeded = false,
            });
            session.Append(new TurnStartPayload(1));
            using var agent = new AgentLoopAgent(context, id, new AgentOptions(), session, _ => 0);
            Task<ToolExecutionResult> Execute() => tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Name = SkillCatalog.ToolName,
                Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { name = "healthy-skill" }),
                Agent = agent, Signal = TestContext.Current.CancellationToken,
            });
            var before = await Execute();
            Assert.False(before.IsError, string.Concat(before.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.Contains("healthy-sibling-body", string.Concat(before.Content.OfType<TextBlock>().Select(block => block.Text)));
            try
            {
                File.CreateSymbolicLink(Path.Combine(root, danglingName), Path.Combine(root, "missing-target.md"));
            }
            catch (Exception error) when (OperatingSystem.IsWindows()
                && error is IOException or UnauthorizedAccessException && (error.HResult & 0xffff) == 1314)
            {
                Assert.Skip("The test account lacks the Windows symbolic-link creation privilege.");
            }
            var after = await Execute();
            Assert.False(after.IsError, string.Concat(after.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.Contains("healthy-sibling-body", string.Concat(after.Content.OfType<TextBlock>().Select(block => block.Text)));
        }
        finally { TempTree.Delete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogTool_ColdAndInvalidatedDiscovery_DoesNotTouchUnselectedBackendsBeforeApproval(bool invalidated)
    {
        await using var fixture = new DiscoveryFixture();
        if (invalidated)
        {
            await fixture.Registry.List(new SkillViewOptions { Cwd = fixture.Root, Signal = TestContext.Current.CancellationToken });
            fixture.ProviderControl.Invalidate();
        }
        fixture.ResetObservations();
        using (ApprovalAnswerers.DenyAll(fixture.Context))
        {
            var denied = await fixture.Execute(TestContext.Current.CancellationToken);
            Assert.True(denied.IsError);
            Assert.Equal((0, 0), (fixture.FileSystem.Reads, fixture.Provider.Reads));
            Assert.Equal(0, fixture.FileSystem.Operations);
            Assert.DoesNotContain("private-discovery-body", string.Concat(denied.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.Single(fixture.Session.SnapshotEvents().Select(item => item.Data).OfType<SafetyDecisionAuditPayload>(),
                audit => audit.Tool == SkillCatalog.ToolName && audit.CallId == fixture.LastCallId.Value && audit.Outcome == "rejected");
        }

        fixture.ResetObservations();
        using (ApprovalAnswerers.AutoApprove(fixture.Context))
        {
            foreach (var name in new[] { "private-file-skill", "unselected-provider-skill" })
            {
                var allowed = await fixture.Execute(TestContext.Current.CancellationToken, name);
                Assert.False(allowed.IsError, string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
                Assert.Contains("private-discovery-body", string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
            }
            Assert.True(fixture.FileSystem.Reads > 0);
            Assert.True(fixture.Provider.Reads > 0);
        }
    }

    [Fact]
    public async Task CatalogTool_CancelledDiscovery_CancelsTheRealCustomFilesystemReader()
    {
        await using var fixture = new DiscoveryFixture(allowTool: true);
        fixture.FileSystem.BlockReads = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var pending = fixture.Execute(cancellation.Token, "private-file-skill");
            var first = await Task.WhenAny(fixture.FileSystem.ReadEntered.Task, pending).WaitAsync(TestContext.Current.CancellationToken);
            if (first == pending)
                Assert.Fail("Discovery finished before entering its controlled reader: "
                    + string.Concat((await pending).Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.Same(fixture.FileSystem.ReadEntered.Task, first);
            cancellation.Cancel();
            fixture.FileSystem.ResumeRead.TrySetResult(true);
            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);
            await fixture.FileSystem.ReadFinished.Task.WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(result.IsError);
            Assert.True(fixture.FileSystem.ReadWasCancelled);
            Assert.Equal(0, fixture.FileSystem.Reads);
            Assert.DoesNotContain("private-discovery-body", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        }
        finally
        {
            cancellation.Cancel();
            fixture.FileSystem.ResumeRead.TrySetResult(true);
        }
    }

    private sealed class DiscoveryFixture : IAsyncDisposable
    {
        private readonly ToolRuntime _tools;
        private readonly AgentLoopAgent _agent;
        private readonly IDisposable _runtimeRegistration;
        private readonly IDisposable _filesystemRegistration;
        private readonly IDisposable _providerRegistration;
        private readonly FileSystemSkillProvider _filesystem;
        private readonly SkillCatalog _catalog;
        private readonly EffectHandle _filesystemService;

        public DiscoveryFixture(bool allowTool = false)
        {
            Root = TempTree.CreateDirectory("skill-discovery-policy");
            var custom = Directory.CreateDirectory(Path.Combine(Root, "custom")).FullName;
            var skill = Directory.CreateDirectory(Path.Combine(custom, "private-file-skill")).FullName;
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: private-file-skill\ndescription: private discovery description\n---\nprivate-discovery-body\n");
            var providerPath = Path.Combine(Root, "provider-content.txt");
            File.WriteAllText(providerPath, "private-discovery-body");
            Context = new Context();
            _ = new SystemPrompt(Context, new SystemPromptConfig());
            _ = new LlmRuntime(Context);
            _tools = new ToolRuntime(Context);
            _ = ApprovalService.Register(Context);
            var home = HarnessHome.Resolve(Path.Combine(Root, "home"));
            Directory.CreateDirectory(home.Root);
            File.WriteAllText(home.SettingsFile, "safety:\n  autoApprove: false\n");
            var settings = HarnessSettings.Load(home);
            if (allowTool) (settings.Safety ??= new SafetySettings()).Whitelist["discovery-invocation"] = new() { Tool = [SkillCatalog.ToolName] };
            settings.Save(home);
            _ = new SafetyBoundary(Context, () => HarnessSettings.Load(home));
            Registry = new SkillRegistry(Context, new SkillRegistryConfig());
            FileSystem = new ObservedSkillFileSystem();
            _filesystemService = Context.Provide("fs", FileSystem);
            FileSystemSkillProvider? filesystem = null;
            SkillProviderControl? providerControl = null;
            _filesystemRegistration = Registry.RegisterProvider(control =>
            {
                providerControl = control;
                return filesystem = new FileSystemSkillProvider(Context, control, new SkillFilesystemConfig
                {
                    ProviderName = "observed-filesystem", IncludeDefaultRoots = false, CustomSkillDirs = [custom], Watch = false,
                });
            });
            _filesystem = filesystem!;
            ProviderControl = providerControl!;
            Provider = new ObservedSkillProvider(providerPath);
            _providerRegistration = Registry.RegisterProvider(_ => Provider);
            _runtimeRegistration = Registry.Register(new SkillRegistration
            {
                Name = "runtime-selected", Description = "Selected public runtime skill", Source = SkillSources.Runtime,
                Content = "public-runtime-selected-body",
            });
            _catalog = new SkillCatalog(Context, Registry);
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            Session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = Root, CreatedAt = 0, IsSeeded = false,
            });
            Session.Append(new TurnStartPayload(1));
            _agent = new AgentLoopAgent(Context, id, new AgentOptions(), Session, _ => 0);
        }

        public string Root { get; }
        public Context Context { get; }
        public Session Session { get; }
        public SkillRegistry Registry { get; }
        public SkillProviderControl ProviderControl { get; }
        public ObservedSkillFileSystem FileSystem { get; }
        public ObservedSkillProvider Provider { get; }
        public ToolCallId LastCallId { get; private set; }

        public Task<ToolExecutionResult> Execute(CancellationToken signal, string name = "runtime-selected") => _tools.Execute(new ToolExecutionInput
        {
            CallId = LastCallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Name = SkillCatalog.ToolName,
            Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { name }), Agent = _agent, Signal = signal,
        });

        public void ResetObservations()
        {
            FileSystem.ResetObservations();
            Provider.Reads = 0;
        }

        public async ValueTask DisposeAsync()
        {
            FileSystem.ResumeRead.TrySetResult(true);
            if (FileSystem.ReadEntered.Task.IsCompleted)
                await FileSystem.ReadFinished.Task.WaitAsync(TestContext.Current.CancellationToken);
            _catalog.Dispose();
            _runtimeRegistration.Dispose();
            _providerRegistration.Dispose();
            _filesystemRegistration.Dispose();
            await _filesystem.Dispose();
            _filesystemService.Dispose();
            _agent.Dispose();
            TempTree.Delete(Root);
        }
    }

    // This adapter observes real fixture filesystem I/O and an explicitly released asynchronous reader.
    private sealed class ObservedSkillFileSystem : ISkillFs
    {
        public int Operations { get; private set; }
        public int Reads { get; private set; }
        public bool BlockReads { get; set; }
        public bool ReadWasCancelled { get; private set; }
        public TaskCompletionSource<bool> ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ResumeRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SkillFsTarget> Resolve(string path)
        {
            Operations++;
            return Task.FromResult(new SkillFsTarget(Path.GetFullPath(path)));
        }

        public Task<IReadOnlyList<SkillFsDirEntry>> ListDir(SkillFsTarget target)
        {
            Operations++;
            IReadOnlyList<SkillFsDirEntry> entries = [.. Directory.EnumerateFileSystemEntries(target.DisplayPath).Select(path =>
                new SkillFsDirEntry(Path.GetFileName(path), Directory.Exists(path) ? SkillFsEntryTypes.Directory : SkillFsEntryTypes.File, new SkillFsTarget(path)))];
            return Task.FromResult(entries);
        }

        public Task<SkillFsInfo?> Stat(SkillFsTarget target, CancellationToken signal)
        {
            signal.ThrowIfCancellationRequested();
            Operations++;
            SkillFsInfo? info = Directory.Exists(target.DisplayPath) ? new(SkillFsEntryTypes.Directory, 0)
                : File.Exists(target.DisplayPath) ? new(SkillFsEntryTypes.File, new FileInfo(target.DisplayPath).Length) : null;
            return Task.FromResult(info);
        }

        public async Task<string> ReadText(SkillFsTarget target, CancellationToken signal)
        {
            Operations++;
            ReadEntered.TrySetResult(true);
            try
            {
                if (BlockReads) await ResumeRead.Task.WaitAsync(signal);
                var text = await File.ReadAllTextAsync(target.DisplayPath, signal);
                Reads++;
                return text;
            }
            catch (OperationCanceledException) when (signal.IsCancellationRequested)
            {
                ReadWasCancelled = true;
                throw;
            }
            finally
            {
                ReadFinished.TrySetResult(true);
            }
        }

        public void ResetObservations()
        {
            Operations = 0;
            Reads = 0;
            ReadWasCancelled = false;
        }
    }

    private sealed class ObservedSkillProvider(string path) : ISkillProvider
    {
        public string Name => "unselected-storage-provider";
        public int Reads { get; set; }

        public async Task<SkillProviderObservation> List(SkillLookupOptions options)
        {
            var description = await File.ReadAllTextAsync(path, options.Signal);
            Reads++;
            return SkillProviderObservation.Full([new SkillCandidate
            {
                Name = "unselected-provider-skill", Description = description, Provider = Name, Source = SkillSources.Custom,
                Invocation = new SkillInvocationPolicy(true, true), Path = path,
            }]);
        }

        public async Task<SkillDefinition?> Get(SkillCandidate candidate, SkillLookupOptions options)
        {
            var content = await File.ReadAllTextAsync(path, options.Signal);
            Reads++;
            return new SkillDefinition
            {
                Name = candidate.Name, Description = candidate.Description, Provider = Name, Source = candidate.Source,
                Invocation = candidate.Invocation, Path = path, Content = content,
            };
        }
    }

    [Fact]
    public async Task Catalog_Is_Injected_As_User_Prefix_Reminder()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        _ = new LlmRuntime(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var skills = new SkillRegistry(ctx, new SkillRegistryConfig());
        using var registration = skills.Register(new SkillRegistration
        {
            Name = "demo-skill",
            Description = "demo",
            Source = "test",
            Content = "demo skill instructions",
        });
        using var catalog = new SkillCatalog(ctx, skills);

        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("demo-provider", "demo-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;
        agent.Followup(MessageFactory.CreateUserText("hi"));
        await agent.WhenIdle();

        var message = agent.Session.DeriveMessages()
            .OfType<UserMessage>()
            .Single(message => message.Source is PluginMessageSource source
                && source.Plugin == SkillCatalog.PluginName);
        Assert.Contains("<available_skills>", Assert.IsType<TextBlock>(message.Content[0]).Text);
        Assert.Contains("demo-skill", Assert.IsType<TextBlock>(message.Content[0]).Text);
    }

    private static string CallSafetyFailure(Session session, ToolCallId callId, ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)) + "\n"
            + string.Join("\n", session.SnapshotEvents().Select(item => item.Data).OfType<SafetyDecisionAuditPayload>()
                .Where(audit => audit.CallId == callId.Value).Select(audit => audit.ToString()));
}
