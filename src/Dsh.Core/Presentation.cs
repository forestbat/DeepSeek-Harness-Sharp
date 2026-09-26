namespace Dsh.Core;

/**
 * 呈现模式传输接缝: 由具体呈现模式的插件注册为服务 "toolPresentation"。
 * native 之外的呈现模式必须有它, 且 IsAvailable 为真, 否则组装提示词即响亮失败(不静默退回 native)。
 */
public interface IToolPresentation
{
    public const string ServiceName = "toolPresentation";

    /** 运行时是否可用: 不可用时非 native 呈现模式不得装配。 */
    bool IsAvailable { get; }

    /** 该呈现模式下唯一可直接调用的工具名, 由提供方给出。 */
    string TransportToolName { get; }

    /** 传输工具定义(名字与 TransportToolName 一致), 由本接缝提供而非注册到工具层。 */
    ToolDefinition TransportDefinition { get; }

    /** 当前 scope 的工具 SDK 声明文本; 不属于该呈现模式时返回空串(空段会被提示词组装丢弃)。 */
    string SdkSection(ScopeKey? scope);

    /** 当前 scope 的传输专属说明文本; 不属于该呈现模式时返回空串。 */
    string TransportOnlySection(ScopeKey? scope);
}
