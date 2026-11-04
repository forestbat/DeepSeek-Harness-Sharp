using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-ptc")]

namespace Dsh.Ptc;

/** Registers run_code alongside native tools and describes the scope-visible SDK. */
public sealed class Plugin : IDshPlugin
{
    public string[] Inject =>
        [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SubprocessService.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var resolved = PtcPluginConfig.Resolve(config);
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("the PTC plugin requires the tools service");
        var registration = ToolRunCode.Register(ctx, Environment.CurrentDirectory, resolved.DebugBatching);
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var sdk = systemPrompt.Section(new PromptSection(
            "tools:sdk",
            context => tools.Get(ToolRunCode.ToolName, context.Scope) is null
                ? ""
                : PtcSdkRenderer.Render(tools.Schemas(context.Scope), resolved.DebugBatching),
            Dynamic: true));
        return new Bundle(registration, sdk);
    }

    private sealed class Bundle(IDisposable registration, IDisposable sdk) : IDisposable
    {
        public void Dispose()
        {
            sdk.Dispose();
            registration.Dispose();
        }
    }
}
