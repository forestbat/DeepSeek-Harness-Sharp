using Cordis;
using Dsh.Interaction;

namespace Dsh.Boot;

public static class PluginCommand
{
    public static IDisposable Register(Context ctx)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        return commands.Register(new CommandDefinition
        {
            Name = "plugins",
            Description = "List known DSH plugin modules",
            Input = new CommandInputDescriptor("list | add <pkg> | remove <pkg>"),
            Handler = invocation =>
            {
                var tokens = invocation.RawInput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0 || tokens[0] == "list")
                {
                    var names = DshBuiltins.All.Keys.OrderBy(name => name, StringComparer.Ordinal);
                    return Task.FromResult<CommandResult>(new CommandResult.Success(string.Join('\n', names)));
                }
                return Task.FromResult<CommandResult>(new CommandResult.Error(
                    "plugin add/remove requires the Batch B profile/plugin subsystem; only \"list\" is available"));
            },
        });
    }
}