using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Dsh.Llm;

public sealed record ToolSchema(string Name, string Description, JsonObject Parameters);

public sealed record LlmCallConfig(
    string Provider,
    string Model,
    ReasoningEffortId? ReasoningEffort = null,
    double? Temperature = null,
    int? MaxTokens = null,
    IReadOnlyList<string>? Stop = null)
{
    public bool Equals(LlmCallConfig? other)
    {
        if (other is null)
            return false;
        if (Provider != other.Provider
            || Model != other.Model
            || ReasoningEffort != other.ReasoningEffort
            || !Temperature.Equals(other.Temperature)
            || MaxTokens != other.MaxTokens)
            return false;
        if (Stop is null || other.Stop is null)
            return Stop is null == other.Stop is null;
        return Stop.SequenceEqual(other.Stop);
    }

    public override int GetHashCode() => HashCode.Combine(Provider, Model, ReasoningEffort, Temperature, MaxTokens);
}

public class GenerateOptions
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public ReasoningEffortId? ReasoningEffort { get; init; }
    public required IReadOnlyList<Message> Messages { get; init; }
    public string? System { get; init; }
    public IReadOnlyList<ToolSchema>? Tools { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public IReadOnlyList<string>? Stop { get; init; }
    public SessionId? SessionId { get; init; }
    public GeneratePurpose? Purpose { get; init; }
    public CancellationToken Cancellation { get; init; }

    /** 请求期注入的附件字节: ImageAttachmentRef.AttachmentId -> 字节。适配器据此把 ImageBlock 序列化为真实图片。 */
    public IReadOnlyDictionary<string, byte[]>? Attachments { get; init; }

    /** 复制请求并替换取消令牌(看门狗需要用一个可自己取消的令牌驱动适配器)。 */
    public GenerateOptions WithCancellation(CancellationToken cancellationToken) => Copy(Messages, cancellationToken);

    /** Copies the request with request-time messages, such as a projection for the target model. */
    public GenerateOptions WithMessages(IReadOnlyList<Message> messages) => Copy(messages, Cancellation);

    private GenerateOptions Copy(IReadOnlyList<Message> messages, CancellationToken cancellationToken) => new()
    {
        Provider = Provider,
        Model = Model,
        ReasoningEffort = ReasoningEffort,
        Messages = messages,
        System = System,
        Tools = Tools,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        Stop = Stop,
        SessionId = SessionId,
        Purpose = Purpose,
        Cancellation = cancellationToken,
        Attachments = Attachments,
    };
}

public enum GeneratePurpose
{
    Compaction,
    SessionTitle,
    Memory,
}

public sealed record LlmProviderInfo(string Id, string Name);

public sealed record LlmModelInfo(
    string Provider,
    string Id,
    string Name,
    string? Description = null,
    IReadOnlyList<string>? InputModalities = null);

public sealed record LlmResolvedModelInfo(
    string Provider,
    string Id,
    string Name,
    string? Description = null,
    IReadOnlyList<string>? InputModalities = null,
    int? ContextWindow = null,
    int? DefaultMaxTokens = null,
    LlmModelReasoningInfo? Reasoning = null,
    string? SystemPromptUpdate = null);

public static class SystemPromptUpdateModes
{
    /** 变化后的系统提示叠在历史尾部(需要供应商接受对话中途的 system 消息), 前缀逐字节不变, 缓存可延续。 */
    public const string InHistory = "in-history";

    /** 变化后的系统提示原地改写开头那条 system 消息; 从改写处起整段前缀按未命中重算。 */
    public const string RewriteHead = "rewrite-head";
}

public sealed record LlmModelReasoningInfo(IReadOnlyList<LlmReasoningEffortInfo> Efforts, ReasoningEffortId? DefaultEffort = null);

public sealed record LlmReasoningEffortInfo(ReasoningEffortId Id, string Name, string? Description = null);

public abstract class LlmAdapter
{
    public abstract LlmProviderInfo ProviderInfo { get; }

    public abstract ResolvedRetryPolicy ProviderRetryPolicy { get; }

    public abstract IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken);

    public virtual IReadOnlyList<LlmModelInfo> ListModels() => [];

    public virtual LlmResolvedModelInfo? ResolveModel(string model) => null;

    public virtual PreparedAdapterCall PrepareCall(string model, CancellationToken cancellationToken)
        => new(model, Stream);
}

public delegate IAsyncEnumerable<StreamChunk> AdapterStreamDelegate(GenerateOptions options, CancellationToken cancellationToken);

public sealed record PreparedAdapterCall(string Model, AdapterStreamDelegate Stream);

/** 标记"由 agent 循环发起"的请求;标记随请求对象一起回收,不随进程累积。 */
public static class AgentLoopRequestMarker
{
    private static readonly ConditionalWeakTable<GenerateOptions, object> Marked = [];
    private static readonly object Flag = new();

    public static T Mark<T>(T request) where T : GenerateOptions
    {
        Marked.AddOrUpdate(request, Flag);
        return request;
    }

    public static bool IsMarked(GenerateOptions request) => Marked.TryGetValue(request, out _);
}
