namespace Dsh.Runtime;

/**
 * settings.yaml 的 tools.progressive_disclosure 与工具运行时之间的接缝。
 * Boot 有配置面、Core 有工具运行时, 两边都只引用 Runtime, 所以开关名放在这里。
 */
public static class ToolDisclosureSwitch
{
    /** Context 属性名; 缺省值 false 表示所有工具直接可见。 */
    public const string PropName = "progressiveToolDisclosure";
}
