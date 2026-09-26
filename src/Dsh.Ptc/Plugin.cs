using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-ptc")]

namespace Dsh.Ptc;

/** PTC 插件: 注册 ptcTransport 服务与 tools:sdk / tools:ptc-only 动态提示词段。 */
public sealed class Plugin : IDshPlugin
{
    public string[] Inject =>
        [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SubprocessService.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var transport = new PtcTransport(ctx, Environment.CurrentDirectory);
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var sdk = systemPrompt.Section(new PromptSection(
            "tools:sdk",
            PromptOrders.ToolsSdk,
            context => transport.SdkSection(context.Scope),
            Dynamic: true));
        var ptcOnly = systemPrompt.Section(new PromptSection(
            "tools:ptc-only",
            PromptOrders.PtcOnly,
            context => transport.PtcOnlySection(context.Scope),
            Dynamic: true));
        return new Bundle(sdk, ptcOnly);
    }

    private sealed class Bundle(IDisposable sdk, IDisposable ptcOnly) : IDisposable
    {
        public void Dispose()
        {
            sdk.Dispose();
            ptcOnly.Dispose();
        }
    }
}
