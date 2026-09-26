using Dsh.Core;

namespace Dsh.Presets;

/**
 * 创造模式运行时能力接缝: 由 Dsh.Inspection 实现并注册为服务 "creativeToolset"。
 * preset 只读地取出工具定义与创作指引段, 在创造模式 scope 内注册; 不引用也不泄漏检查插件的实现。
 */
public interface ICreativeToolset
{
    public const string ServiceName = "creativeToolset";

    /** 创造模式独占的工具定义; 由 preset 注册到 agent scope。 */
    IReadOnlyList<ToolDefinition> Tools { get; }

    /** 创作指引提示词段; 由 preset 注册到 agent scope。 */
    PromptSection GuidanceSection { get; }

    /** 运行时是否可用: 不可用时创造模式不得装配。 */
    bool IsAvailable { get; }
}
