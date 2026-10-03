using System.Text.Json.Nodes;

namespace Dsh.Core;

/** TOON 编码能力的消费侧契约: 由 @deepseek-ai/dsh-toon 插件实现; 未装该插件时 toon 输出格式不可用。 */
public interface IToonCodec
{
    public const string ServiceName = "toon-codec";

    string Encode(JsonNode? value);
}
