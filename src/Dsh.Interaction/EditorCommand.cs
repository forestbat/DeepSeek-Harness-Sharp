using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Interaction;

public static class EditorCommand
{
    public static IDisposable Register(Context ctx, HarnessHome home)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false)!;
        return commands.Register(new CommandDefinition
        {
            Name = "editor",
            Description = "List or launch a configured external editor",
            Input = new CommandInputDescriptor("list | launch <path> [editor]"),
            Handler = invocation => RunAsync(ctx, home, invocation),
        });
    }

    private static Task<CommandResult> RunAsync(Context ctx, HarnessHome home, CommandInvocation invocation)
    {
        var parts = invocation.RawInput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var settings = HarnessSettings.Load(home);
        switch (parts)
        {
            case [] or ["list"]:
                if (settings.ExternalEditor.Count == 0)
                    return Task.FromResult<CommandResult>(new CommandResult.Success(
                        "no external_editor configured; the platform default editor will be used"));
                var lines = settings.ExternalEditor.Select((editor, index) =>
                    $"{index + 1}. {(string.IsNullOrWhiteSpace(editor.Name) ? editor.Command : editor.Name)}{(index == 0 ? " (favorite)" : "")}  {editor.Command}");
                return Task.FromResult<CommandResult>(new CommandResult.Success(string.Join('\n', lines)));
            case ["launch", var path, .. var rest]:
                try
                {
                    var launcher = ctx.Get<IExternalEditorLauncher>(IExternalEditorLauncher.ServiceName, false)
                        ?? throw new InvalidOperationException("external editor tool plugin is not loaded");
                    var launch = launcher.Launch(path, rest.Length > 0 ? rest[0] : null);
                    return Task.FromResult<CommandResult>(new CommandResult.Success(
                        $"launched {launch.Editor} for {launch.Command} (PID {launch.ProcessId})"));
                }
                catch (Exception error)
                {
                    return Task.FromResult<CommandResult>(new CommandResult.Error(error.Message));
                }
            default:
                return Task.FromResult<CommandResult>(new CommandResult.Error("usage: /editor list | /editor launch <path> [editor]"));
        }
    }
}
