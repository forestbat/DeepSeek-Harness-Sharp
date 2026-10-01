using Dsh.Runtime;
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
}
