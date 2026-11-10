using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Boot;

/// <summary>Creates the optional execution-aware host and registers its owned resources for reverse-order disposal.</summary>
public delegate PluginHost? PluginRuntimeFactory(Context context,
    PluginExecutionEnvironment environment, string directory, Func<INativePlugin, IDshPlugin>? nativeBridge,
    Action<IDisposable> track);
