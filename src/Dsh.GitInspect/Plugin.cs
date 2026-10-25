using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-git-inspect")]

namespace Dsh.GitInspect;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public Task<IDisposable?> ApplyAsync(Context ctx, object? config, CancellationToken cancellationToken = default)
    {
        var service = new GitService();
        ctx.Provide(IGitService.ServiceName, service);
        return Task.FromResult<IDisposable?>(new NoopDisposable());
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
