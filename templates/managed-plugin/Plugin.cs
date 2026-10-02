// 一个插件 = 一个实现 IDshPlugin 的类, 加上 csproj 里那行生成器 props import, 产物连依赖放进 plugins/<目录名>/ 即被宿主发现。
// 包名声明是可选的: 不写 [assembly: DshPlugin] 时包名取 AssemblyName, 需要符合包名约定(如 @scope/name)时再显式声明。
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@example/dsh-plugin-template")]

namespace DshPluginTemplate;

/**
 * 插件类是组合单元: Apply 安装效果(服务/工具/提示词段/事件订阅), 返回的 IDisposable 在卸载时回收这些效果。
 * 一个程序集只能有一个 IDshPlugin 实现(DSHPLUGIN001)。
 * 构造函数只支持两种形状: 公共无参, 或公共 (string packageName)。
 * 后者用于一个程序集装多个包(多包模式): 生成器为每个声明的包名各 new 一次, Apply 里按 packageName 分派。
 */
public sealed class Plugin : IDshPlugin
{
    /**
     * Inject 声明服务图边: 本插件需要的服务名。
     * 宿主用它做两件事: 拓扑排序决定 Apply 先后; 服务被重新提供时反查依赖方触发热重载。
     * 依赖缺失时插件停在 Pending 并在启动 WARN 里列出缺什么、谁能提供, 不会静默失败。
     */
    public string[] Inject => [SystemPrompt.ServiceName];

    /**
     * Apply 是效果安装点, 在依赖全部就绪后被调用。
     * ctx.Get<T>(服务名) 取依赖的服务; config 是 settings.yaml 里 plugins.<包名>.parameters 的参数表。
     * 这里注册一个提示词段作为示范: 段名全局唯一, 未声明 After/Before 的段按注册序追加在尾部。
     */
    public IDisposable Apply(Context ctx, object? config)
    {
        var prompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        return prompt.Section(PromptSection.Literal(
            "template:hello",
            "This deployment includes the example template plugin."));
    }
}

/*
 * 工具命名约定: 工具名是 LLM 可见的标识, 保持短小。
 * 名字是合法 C# 标识符时, PTC 模式下可直接以 tools.<name>(args) 调用。
 * 不是合法标识符(如含连字符)时, PTC 里要走 tools.call("<name>", args), 注册时宿主会 WARN 提醒。
 * 两个插件注册了同名工具时不再报错: 后注册者自动改名为 <包名末段>-<工具名> 并 WARN 告知双方作者。
 *
 * 共享依赖判据: 你的某个程序集的类型会跨插件边界流动(作为服务返回值、事件载荷、共享基类)时,
 * 用 [assembly: DshSharedDependency("程序集名")] 声明, 该程序集不拷进插件目录, 由全进程共享池加载。
 * 纯内部实现一律私有, 各带一份反而干净。
 */
