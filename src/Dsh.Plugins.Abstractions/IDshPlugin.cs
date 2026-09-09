using Cordis;

namespace Dsh.Plugins;

public interface IDshPlugin
{
    string[] Inject { get; }
    IDisposable Apply(Context ctx, object? config);
}
