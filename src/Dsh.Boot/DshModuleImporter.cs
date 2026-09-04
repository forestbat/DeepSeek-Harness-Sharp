using Cordis;
using Cordis.Loader;
using Cordis.Node;

namespace Dsh.Boot;

public sealed class DshModuleImporter(NodeImporter fallback) : IModuleImporter
{
    public Task<object?> Import(string specifier, string? baseUrl)
        => DshBuiltins.All.TryGetValue(specifier, out var definition)
            ? Task.FromResult<object?>(definition)
            : fallback.Import(specifier, baseUrl);

    public ValueTask<object?> Evaluate(Context ctx, string expr) => fallback.Evaluate(ctx, expr);
}
