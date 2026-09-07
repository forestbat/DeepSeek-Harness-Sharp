using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Skills;

namespace Dsh.Boot;

public static class SkillCommand
{
    public static IDisposable Register(Context ctx)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        return commands.Register(new CommandDefinition
        {
            Name = "skill",
            Description = "List or inspect a skill",
            Input = new CommandInputDescriptor("name"),
            Handler = async invocation =>
            {
                var registry = ctx.Get<SkillRegistry>(SkillRegistry.ServiceName);
                if (registry is null)
                    return new CommandResult.Error("skills service is not available");
                var raw = invocation.RawInput.Trim();
                if (raw.Length == 0)
                {
                    var skills = await registry.List(new SkillViewOptions { Signal = invocation.Signal });
                    if (skills.Count == 0)
                        return new CommandResult.Success("no skills available");
                    return new CommandResult.Success(string.Join('\n', skills.Select(skill => $"{skill.Name}: {skill.Description}")));
                }
                var skill = await registry.Get(raw, new SkillViewOptions { Signal = invocation.Signal });
                if (skill is null)
                    return new CommandResult.Error($"skill \"{raw}\" not found");
                return new CommandResult.Success($"{skill.Name}: {skill.Description}");
            },
        });
    }
}