using Cordis;
using Dsh.Interaction;

namespace Dsh.Goal;

public static class GoalCommand
{
    public static IDisposable Register(Context ctx)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        return commands.Register(new CommandDefinition
        {
            Name = "goal",
            Description = "Show, create, or clear the current goal",
            Input = new CommandInputDescriptor("show | <objective> | clear"),
            Handler = async invocation =>
            {
                var agent = invocation.Agent;
                var goals = ctx.Get<GoalService>(GoalService.ServiceName, false)!;
                var raw = invocation.RawInput.Trim();
                try
                {
                    if (raw.Length == 0 || raw == "show")
                    {
                        var current = goals.Get(agent);
                        return new CommandResult.Success(current is null
                            ? "no current goal"
                            : $"{current.Id}: {current.Objective} (phase={current.Phase})");
                    }
                    if (raw == "clear")
                    {
                        var current = goals.Get(agent);
                        if (current is null)
                            return new CommandResult.Error("there is no current goal to clear");
                        goals.Clear(agent, new GoalRef(current.Id, current.Revision));
                        return new CommandResult.Success("goal cleared");
                    }
                    var created = goals.Create(agent, new CreateGoalRequest(raw));
                    return new CommandResult.Success($"goal set: {created.Objective}");
                }
                catch (GoalException error)
                {
                    return new CommandResult.Error($"{error.Code}: {error.Message}");
                }
            },
        });
    }
}
