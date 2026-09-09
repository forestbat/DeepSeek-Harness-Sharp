using Cordis;
using Dsh.Interaction;
using Dsh.Plugins;

namespace Dsh.Boot;

public static class PluginCommand
{
    public static IDisposable Register(Context ctx, PluginCatalog catalog)
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
                    var names = catalog.PackageNames
                        .Distinct()
                        .OrderBy(name => name, StringComparer.Ordinal);
                    return Task.FromResult<CommandResult>(new CommandResult.Success(string.Join('\n', names)));
                }
                return Task.FromResult<CommandResult>(new CommandResult.Error(
                    "plugin add/remove requires the Batch B profile/plugin subsystem; only \"list\" is available"));
            },
        });
    }
}