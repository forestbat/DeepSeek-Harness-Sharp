using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Boot;

public delegate IPluginRuntime? PluginRuntimeFactory(Context context,
    PluginExecutionEnvironment environment, PluginHost host, string directory, Func<INativePlugin, IDshPlugin>? nativeBridge);
