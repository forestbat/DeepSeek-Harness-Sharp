using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-remote-host-server")]

namespace Dsh.RemoteHost;

public sealed class RemoteHostServerPlugin : IDshPlugin
{
    public string[] Inject => [];

    public Task<IDisposable?> ApplyAsync(Context ctx, object? config, CancellationToken cancellationToken = default)
    {
        ctx.Provide(RemoteHostServerFactory.ServiceName, new RemoteHostServerFactory());
        return Task.FromResult<IDisposable?>(new NoopDisposable());
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
