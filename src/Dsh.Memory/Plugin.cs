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
        var cwd = options.Cwd ?? Environment.CurrentDirectory;
        var store = MemoryStoreFactory.Create(config as MemoryPluginConfig ?? MemoryPluginConfig.Resolve(config), cwd);
        var memory = new ProjectMemory(store, ProjectMemory.SidecarDirFor(ProjectRoot.Resolve(cwd)));
        ctx.Provide(MemoryServices.Store, store);
        ctx.Provide(MemoryServices.ProjectMemory, memory);
        var tool = MemorySaveTool.Register(ctx, memory);
        var capture = new MemoryCapture(ctx, memory, options);
        return new Bundle(store as IDisposable, tool, capture);
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
