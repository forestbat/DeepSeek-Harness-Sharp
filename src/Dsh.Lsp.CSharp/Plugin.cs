using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-lsp-csharp")]

namespace Dsh.Lsp.CSharp;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [LspProviderRegistry.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var registry = ctx.Get<LspProviderRegistry>(LspProviderRegistry.ServiceName)
            ?? throw new InvalidOperationException("lspProviders is required for the C# LSP adapter");
        return registry.Register(
            "csharp-roslyn",
            new CSharpSyntaxTreeProvider(),
            new CSharpTypeProvider(),
            new CSharpFlowProvider(),
            new CSharpDeclarationProvider(),
            new CSharpReferenceProvider(),
            languageIds: ["csharp", "c#"],
            priority: 100);
    }
}
