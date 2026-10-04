using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-memory")]

namespace Dsh.Memory;

/** 项目记忆插件:按 settings.yaml 提供 IMemoryStore 与 ProjectMemory,注册 memory_save 工具与回合末自动捕获。 */
public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, LlmRuntime.ServiceName, SessionStore.ServiceName];

    public Type ConfigType => typeof(MemoryPluginConfig);

    public IDisposable Apply(Context ctx, object? config)
    {
        var options = ctx.GetProp("harnessOptions") as HarnessOptions
            ?? throw new InvalidOperationException("harnessOptions is required for the memory plugin");
        var workspace = new MemoryWorkspace(config as MemoryPluginConfig ?? MemoryPluginConfig.Resolve(config));
        ctx.Provide(MemoryServices.Provider, workspace);
        var tool = MemorySaveTool.Register(ctx, exec => workspace.For(exec.Agent?.Session.Header.Cwd));
        var capture = new MemoryCapture(ctx, session => workspace.For(session.Header.Cwd), options);
        return new Bundle(workspace, tool, capture);
    }

    private sealed class Bundle(IDisposable? store, IDisposable tool, IDisposable capture) : IDisposable
    {
        public void Dispose()
        {
            capture.Dispose();
            tool.Dispose();
            store?.Dispose();
        }
    }
}
