using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Skills;
using Dsh.Agent;

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
            var policy = new SafetySettings { Whitelist = new() { ["skill-invocation"] = new() { Tool = [SkillCatalog.ToolName] } } };
            await using var fixture = new DiscoveryFixture(root, [root], () => new HarnessSettings { Safety = policy });
            var allowed = await fixture.Execute(TestContext.Current.CancellationToken, "private-skill");
            Assert.False(allowed.IsError, string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.Contains("private-skill-body-marker", string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
            policy.Blacklist["deny-skill-file"] = new() { Path = path, Intent = "read" };
            var denied = await fixture.Execute(TestContext.Current.CancellationToken, "private-skill");
            Assert.True(denied.IsError);
            Assert.DoesNotContain("private-skill-body-marker", string.Concat(denied.Content.OfType<TextBlock>().Select(block => block.Text)));
            SafetyAssert.Denied(fixture.Session, fixture.LastCallId.Value, "deny-skill-file", path, "read", SkillCatalog.ToolName);
            var manual = await fixture.Registry.Get("private-skill", new SkillViewOptions { Cwd = root, Signal = TestContext.Current.CancellationToken });
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

            var policy = new SafetySettings { Whitelist = new() { ["skill-call"] = new() { Tool = [SkillCatalog.ToolName] } } };
            await using var fixture = new DiscoveryFixture(root, [skills], () => new HarnessSettings { Safety = policy });
            var manual = await fixture.Registry.Get("linked-skill", new SkillViewOptions { Cwd = root, Signal = TestContext.Current.CancellationToken });
            Assert.NotNull(manual);
            Assert.Contains("linked-private-body", manual.Content);
            var allowed = await fixture.Execute(TestContext.Current.CancellationToken, "linked-skill");
            Assert.False(allowed.IsError, string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.Contains("linked-private-body", string.Concat(allowed.Content.OfType<TextBlock>().Select(block => block.Text)));

            policy.Blacklist["deny-linked-target"] = new() { Path = target, Intent = "read" };
            var denied = await fixture.Execute(TestContext.Current.CancellationToken, "linked-skill");
            Assert.True(denied.IsError);
            Assert.DoesNotContain("linked-private-body", string.Concat(denied.Content.OfType<TextBlock>().Select(block => block.Text)));
            SafetyAssert.Denied(fixture.Session, fixture.LastCallId.Value, "deny-linked-target", target, "read", SkillCatalog.ToolName);
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
            var policy = new SafetySettings { Whitelist = new() { ["skill-call"] = new() { Tool = [SkillCatalog.ToolName] } } };
            await using var fixture = new DiscoveryFixture(root, [root], () => new HarnessSettings { Safety = policy });
            var before = await fixture.Execute(TestContext.Current.CancellationToken, "healthy-skill");
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
            var after = await fixture.Execute(TestContext.Current.CancellationToken, "healthy-skill");
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

}
