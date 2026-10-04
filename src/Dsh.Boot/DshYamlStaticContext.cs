using YamlDotNet.Serialization;

namespace Dsh.Boot;

[YamlStaticContext]
[YamlSerializable(typeof(HarnessSettings))]
[YamlSerializable(typeof(ProviderSettings))]
[YamlSerializable(typeof(ProviderOptions))]
[YamlSerializable(typeof(ProviderModelSettings))]
[YamlSerializable(typeof(SubagentSettings))]
[YamlSerializable(typeof(SkillsSettings))]
[YamlSerializable(typeof(CompactionSettings))]
[YamlSerializable(typeof(SafetySettings))]
[YamlSerializable(typeof(McpServerSettings))]
[YamlSerializable(typeof(A2aSettings))]
[YamlSerializable(typeof(A2aRemoteSettings))]
[YamlSerializable(typeof(LoggingSettings))]
public partial class DshYamlStaticContext : StaticContext
{
}