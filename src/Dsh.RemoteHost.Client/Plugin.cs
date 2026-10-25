using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-remote-host-client")]

namespace Dsh.RemoteHost;

public sealed class RemoteHostClientPlugin : IDshPlugin
{
    public string[] Inject => [];

    public Task<IDisposable?> ApplyAsync(Context ctx, object? config, CancellationToken cancellationToken = default)
    {
        ctx.Provide(RemoteHostClientService.ServiceName, new RemoteHostClientService());
        return Task.FromResult<IDisposable?>(null);
    }
}
