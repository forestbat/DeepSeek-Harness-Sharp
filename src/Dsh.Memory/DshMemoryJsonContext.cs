using System.Text.Json.Serialization;

namespace Dsh.Memory;

/**
 * 记忆审计行的序列化上下文: 审计文件格式与 Core 的会话事件无关, 因此不共用 DshCoreJsonContext。
 * 选项必须与 Core 上下文保持一致(CamelCase + 省略 null), 否则已有的审计文件读不出来。
 */
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MemoryAuditEntry))]
[JsonSerializable(typeof(MemorySaveResult))]
internal sealed partial class DshMemoryJsonContext : JsonSerializerContext;
