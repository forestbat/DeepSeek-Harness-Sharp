using Dsh.Boot;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Workspace;

namespace Dsh.Gui.Services;

/** Synchronizes the standalone GUI process's local plugin manager when Host plugin commands mutate shared local state. */
public sealed class GuiPluginCoordinator(Context ctx, HostChatSession chat)
{
    private readonly record struct PluginMutation(bool IsMutation, string Subcommand, string Package);

    public async Task<string> RunAsync(string line)
    {
        var result = await ExecuteAsync(line);
        return result.Known ? result.Text ?? "" : $"未知命令: {line}";
    }

    public async Task<RemoteCommandResult> ExecuteAsync(string line)
    {
        var mutation = Parse(line);
        if (!mutation.IsMutation || ResolveLocalGuiManager() is not { } localManager)
            return await chat.ExecuteAsync(line);
        await DetachBeforeHostMutationAsync(localManager, mutation);
        try
        {
            return await chat.ExecuteAsync(line);
        }
        finally
        {
            await localManager.SynchronizeWithDiskAsync();
        }
    }

    private HarnessPluginManager? ResolveLocalGuiManager()
    {
        if (ctx.GetProp("hostBackendInProcess") is true || chat.Machine != MachineSpec.LocalName)
            return null;
        return ctx.Get<HarnessPluginManager>("pluginManager", false);
    }

    private static Task DetachBeforeHostMutationAsync(HarnessPluginManager manager, PluginMutation mutation)
    {
        if (mutation.Package.Length == 0 || mutation.Subcommand is not ("remove" or "reload" or "disable"))
            return Task.CompletedTask;
        return manager.DetachForExternalMutationAsync(mutation.Package, reclaimArtifact: mutation.Subcommand is "remove" or "reload");
    }

    private static PluginMutation Parse(string line)
    {
        var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !string.Equals(tokens[0], "/plugins", StringComparison.Ordinal))
            return default;
        var subcommand = tokens[1];
        if (subcommand is not ("add" or "remove" or "enable" or "disable" or "reload"))
            return default;
        var package = subcommand == "remove"
            ? tokens.Skip(2).FirstOrDefault(token => token != "--force") ?? ""
            : tokens.Length > 2 ? tokens[2] : "";
        return new PluginMutation(true, subcommand, package);
    }
}
