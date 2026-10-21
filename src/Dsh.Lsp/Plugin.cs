using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Tools;
using Dsh.Lsp.TreeSitter;

[assembly: DshPlugin("@deepseek-ai/dsh-lsp")]

namespace Dsh.Lsp;

/** LSP 是能力, 不是入口; 有工具运行时时注册 lsp_* 工具。 */
public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        var registry = ctx.Get<LspProviderRegistry>(LspProviderRegistry.ServiceName, false)
            ?? new LspProviderRegistry(ctx);
        var treeSitter = TreeSitterConfig.CreateProvider(config);
        var providerRegistration = registry.Register(
            "tree-sitter",
            syntaxTree: treeSitter,
            zone: new LanguageZoneProvider());
        var roslynRegistration = RoslynProbe.TryLoad(ctx, out _);
        var pythonRegistration = CompilerAdapterProbe.TryLoad(ctx, "Dsh.Lsp.Python.dll", out _);
        return new Registration(
            LspToolSet.Register(ctx, registry),
            providerRegistration,
            roslynRegistration,
            pythonRegistration);
    }

    private sealed class Registration(
        IDisposable tools,
        IDisposable? providers,
        IDisposable? roslyn,
        IDisposable? python) : IDisposable
    {
        public void Dispose()
        {
            roslyn?.Dispose();
            python?.Dispose();
            providers?.Dispose();
            tools.Dispose();
        }
    }
}
