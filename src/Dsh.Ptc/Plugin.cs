using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-ptc")]

namespace Dsh.Ptc;

/** PTC 插件: 注册 toolPresentation 服务与 tools:sdk / tools:ptc-only 动态提示词段。 */
public sealed class Plugin : IDshPlugin
{
    private const int ToolsSdkOrder = 5000;
    private const int PtcOnlyOrder = 800;

    public string[] Inject =>
        [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SubprocessService.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("the PTC plugin requires the tools service");
        tools.ReserveName(PtcTransport.RunCodeName, "the presentation transport tool");
        var transport = new PtcTransport(ctx, Environment.CurrentDirectory);
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var sdk = systemPrompt.Section(new PromptSection(
            "tools:sdk",
            ToolsSdkOrder,
            context => transport.SdkSection(context.Scope),
            Dynamic: true));
        var ptcOnly = systemPrompt.Section(new PromptSection(
            "tools:ptc-only",
            PtcOnlyOrder,
            context => transport.TransportOnlySection(context.Scope),
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
