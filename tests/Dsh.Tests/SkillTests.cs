using Cordis;
using Dsh.Core;
using Dsh.Skills;

namespace Dsh.Tests;

public class SkillTests
{
    [Fact]
    public async Task FileSystemProvider_Discovers_And_Loads_Directory_Skill()
    {
        var ctx = new Context();
        _ = new SkillRegistry(ctx, new SkillRegistryConfig());        var root = Path.Combine(Path.GetTempPath(), $"dsh-skills-{Guid.NewGuid():N}");
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
                """);
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
            Assert.Contains("Follow the alpha protocol", definition!.Content);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
