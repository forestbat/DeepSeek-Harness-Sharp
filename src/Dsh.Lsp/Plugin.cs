using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Lsp.Tools;
using Dsh.Lsp.TreeSitter;

[assembly: DshPlugin("@deepseek-ai/dsh-lsp")]

namespace Dsh.Lsp;

/** LSP 是能力, 不是入口; 有工具运行时时注册 lsp_* 工具。 */
public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
        => LspToolSet.Register(ctx, TreeSitterConfig.CreateProvider(config));
}
