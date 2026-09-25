using Dsh.Runtime;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Presets.Plugin.Presets)]

namespace Dsh.Presets;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Presets = "@deepseek-ai/dsh-presets";

    public string[] Inject => packageName switch
    {
        Presets => [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Presets => RegisterPresets(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterPresets(Context ctx)
    {
        _ = new PresetController(ctx);
        return new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
