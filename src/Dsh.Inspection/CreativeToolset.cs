using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Inspection;

/** 创造模式接缝实现: 聚合 cordis 只读检查工具、plugin_manager 管理工具与创作指引段。
 *  工具不再全局注册, 由 preset 在创造模式 scope 内按定义注册; 审批门在服务注册时全局生效。 */
public sealed class CreativeToolset : Service, ICreativeToolset
{
    private const string GuidanceText = """
        Creative mode adds live runtime inspection and plugin management on top of the standard toolset:
        - cordis_inspect_list / cordis_inspect_query: read-only inspection of services, settings sections, and the tools visible to this agent; the event category is reported unavailable in this build.
        - plugin_manager: list, describe, enable, disable, add, or remove loaded plugins; every call asks the user for approval, and denied operations do nothing.
        """;

    private CreativeToolset(Context ctx) : base(ctx, ICreativeToolset.ServiceName)
    {
        Tools = [.. ToolCordis.Definitions(ctx), ToolPluginManager.Definition(ctx)];
        GuidanceSection = PromptSection.Literal("creative:guidance", InspectionPromptOrders.Creative, GuidanceText);
    }

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public PromptSection GuidanceSection { get; }

    public bool IsAvailable => true;

    /** 由 Dsh.Inspection 的插件包注册: 已由任一包注册则改为幂等。 */
    public static IDisposable Register(Context ctx)
    {
        if (ctx.Get<ICreativeToolset>(ICreativeToolset.ServiceName, false) is not null)
            return new ActionDisposable(() => { });
        _ = new CreativeToolset(ctx);
        return ToolPluginManager.ApprovalGate(ctx);
    }
}
