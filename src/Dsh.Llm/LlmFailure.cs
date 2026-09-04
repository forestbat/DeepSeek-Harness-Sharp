namespace Dsh.Llm;

public sealed record LlmFailure(
    string Message,
    string Code,
    int? Status = null,
    long? ProviderRetryAfterMs = null,
    ProviderRequestId? RequestId = null);
