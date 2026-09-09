using Cordis;
using Cordis.Loader;
using Dsh.Plugins;

namespace Dsh.Boot;

public sealed class DshModuleImporter(PluginCatalog? plugins = null) : IModuleImporter
{
    public Task<object?> Import(string specifier, string? baseUrl)
    {
        if (plugins?.TryCreateDefinition(specifier, out var plugin) == true)
            return Task.FromResult<object?>(plugin!);
        throw new CordisException("PLUGIN_NOT_FOUND", $"plugin not found: {specifier}");
    }

    public ValueTask<object?> Evaluate(Context ctx, string expr) =>
        throw new CordisException("EVAL_UNSUPPORTED", "JS expression evaluation is not supported; plugins are C# only.");
}
