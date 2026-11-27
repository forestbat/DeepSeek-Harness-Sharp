using Dsh.Boot;
using Dsh.Interaction;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Workspace;

namespace Dsh.Gui.Services;

/** Synchronizes the standalone GUI process's local plugin manager when Host plugin commands mutate shared local state. */
public sealed class GuiPluginCoordinator(Context ctx, HostChatSession chat)
{
    public async Task<string> RunAsync(string line)
    {
        var result = await ExecuteAsync(line);
        return result.Known ? result.Text ?? "" : $"未知命令: {line}";
    }

    public async Task<RemoteCommandResult> ExecuteAsync(string line)
    {
        if (PluginCommand.ParseMutationCommand(line) is not { } mutation || ResolveLocalGuiManager() is not { } localManager)
            return await chat.ExecuteAsync(line);
        if (await DetachBeforeHostMutationAsync(localManager, mutation) is { } error)
            return new RemoteCommandResult(Known: true, Text: error, FollowupPrompt: null);
        try
        {
            return await chat.ExecuteAsync(line);
        }
        finally
        {
            var discoverArtifacts = mutation.Subcommand == "add" || mutation.ArtifactPath is not null;
            await localManager.SynchronizeWithDiskAsync(discoverArtifacts: discoverArtifacts);
        }
    }

    private HarnessPluginManager? ResolveLocalGuiManager()
    {
        if (ctx.GetProp("hostBackendInProcess") is true || chat.Machine != MachineSpec.LocalName)
            return null;
        return ctx.Get<HarnessPluginManager>("pluginManager", false);
    }

    private static Task<string?> DetachBeforeHostMutationAsync(HarnessPluginManager manager, PluginCommand.Mutation mutation)
    {
        if (mutation.Subcommand is not ("remove" or "reload" or "disable"))
            return Task.FromResult<string?>(null);
        return manager.DetachForExternalMutationAsync(mutation.Target, reclaimArtifact: mutation.Subcommand is "remove" or "reload");
    }
}
