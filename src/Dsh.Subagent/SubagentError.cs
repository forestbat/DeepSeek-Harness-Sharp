using Dsh.Llm;

namespace Dsh.Subagent;

public static class SubagentErrorCodes
{
    public const string NoProvider = "NO_PROVIDER";
    public const string DuplicateProvider = "DUPLICATE_PROVIDER";
    public const string UnsupportedCapability = "UNSUPPORTED_CAPABILITY";
    public const string DepthExceeded = "DEPTH_EXCEEDED";
    public const string ContinuationUnavailable = "CONTINUATION_UNAVAILABLE";
    public const string SessionStoreUnavailable = "SESSION_STORE_UNAVAILABLE";
}

public sealed class SubagentException(string message, string code) : HarnessException(message, code);

public sealed class SubagentDepthError(string message) : HarnessException(message, SubagentErrorCodes.DepthExceeded);
