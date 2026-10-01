using Dsh.Runtime;
using Dsh.Interaction;

namespace Dsh.Skills;

/**
 * /skill:NAME 提示词: 把该 skill 的指导与用户提示词一起交给模型走下一步。
 * 菜单选中 skill 会回填空格形式(/skill NAME 提示词), 与冒号形式等价。
 */
public static class SkillCommand
{
    public static IDisposable Register(Context ctx)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        return commands.Register(new CommandDefinition
        {
            Name = "skill",
            Description = "Run a skill for the given prompt",
            Input = new CommandInputDescriptor("<name> <prompt>"),
            AcceptsPrompt = true,
            Handler = async invocation =>
            {
                var registry = ctx.Get<SkillRegistry>(SkillRegistry.ServiceName, false);
                if (registry is null)
                    return new CommandResult.Error("skills service is not available");
                var raw = invocation.RawInput.Trim();
                if (raw.Length == 0)
                    return new CommandResult.Error("usage: /skill:<name> <prompt>");
                var separator = raw.IndexOf(' ');
                var name = separator < 0 ? raw : raw[..separator];
                var prompt = separator < 0 ? "" : raw[(separator + 1)..].Trim();
                if (prompt.Length == 0)
                    return new CommandResult.Error($"usage: /skill:{name} <prompt> — a prompt is required");
                var skill = await registry.Get(name, new SkillViewOptions { Signal = invocation.Signal });
                if (skill is null)
                    return new CommandResult.Error($"skill \"{name}\" not found", prompt);
                var content = SkillRender.RenderSkillContent(skill.Name, skill.Provider, skill.ResourceBase, skill.Content);
                return new CommandResult.Success($"skill {skill.Name} activated", FollowupPrompt: $"{content}\n\n{prompt}");
            },
        });
    }
}
