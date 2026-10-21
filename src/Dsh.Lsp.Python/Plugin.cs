using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-lsp-python")]

namespace Dsh.Lsp.Python;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [LspProviderRegistry.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var registry = ctx.Get<LspProviderRegistry>(LspProviderRegistry.ServiceName)
            ?? throw new InvalidOperationException("lspProviders is required for the Python LSP adapter");
        return registry.Register(
            "python-ast",
            structuredType: new PythonTypeProvider(),
            flow: new PythonFlowProvider(),
            declarations: new PythonDeclarationProvider(),
            references: new PythonReferenceProvider(),
            languageIds: ["python"]);
    }
}
