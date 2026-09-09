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
[YamlSerializable(typeof(MemorySettings))]
[YamlSerializable(typeof(McpServerSettings))]
public partial class DshYamlStaticContext : StaticContext
{
}