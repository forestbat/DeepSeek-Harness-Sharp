using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Sdk;
using Dsh.Lsp.Tools;

[assembly: DshPlugin("@deepseek-ai/dsh-lsp")]

namespace Dsh.Lsp;

/** 入口 "lsp":在 stdio 上跑 LSP 服务; 同时注册 lsp_* 工具。 */
[DshEntrypoint("lsp")]
public sealed class Plugin : IDshPlugin, IDshEntrypoint
{
    public string[] Inject => [ToolRuntime.ServiceName];

    public IDisposable Apply(Context ctx, object? config) => LspToolSet.Register(ctx);

    public async Task<int> RunAsync(HarnessApp app, PluginEntrypointOptions options, CancellationToken cancellationToken)
    {
        await using var transport = new JsonRpcLineTransport(Console.In, Console.Out);
        transport.RequestHandler = new LspServer().HandleRequestAsync;
        transport.Start();
        await transport.WhenClosedAsync().WaitAsync(cancellationToken);
        return 0;
    }

}
