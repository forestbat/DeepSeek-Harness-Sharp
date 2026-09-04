using System.Text.RegularExpressions;

namespace Dsh.Llm;

public class HarnessException : Exception
{
    public string Code { get; }

    public HarnessException(string message, string code, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }
}

public sealed class LlmException : HarnessException
{
    public LlmFailure Failure { get; }

    public LlmException(LlmFailure failure, Exception? innerException = null)
        : base(failure.Message, failure.Code, innerException)
    {
        Failure = failure;
    }
}

public static class LlmFailureCodes
{
    public const string ContextWindowExceeded = "CONTEXT_WINDOW_EXCEEDED";
    public const string Quota = "QUOTA";
    public const string EmptyResponse = "EMPTY_RESPONSE";
    public const string InvalidCredential = "INVALID_CREDENTIAL";
    public const string MissingCredential = "MISSING_CREDENTIAL";
    public const string NoAdapter = "NO_ADAPTER";
    public const string RateLimit = "RATE_LIMIT";
    public const string Server = "SERVER";
    public const string Timeout = "TIMEOUT";
    public const string Transport = "TRANSPORT";
    public const string StreamClosed = "STREAM_CLOSED";
}

public static partial class LlmFailureClassifiers
{
    [GeneratedRegex("""(?:^|[^a-z0-9])context[\s_-](?:length|window)[\s_-](?:exceed(?:ed|s)?|overflow(?:ed)?|limit[\s_-]exceeded)(?:$|[^a-z0-9])""", RegexOptions.IgnoreCase)]
    private static partial Regex StructuredContextOverflow();

    [GeneratedRegex("""\b(?:maximum|max)(?:\s+(?:allowed|supported))?\s+context\s+(?:length|window)\b""", RegexOptions.IgnoreCase)]
    private static partial Regex MaximumContextLength();

    [GeneratedRegex("""\b(?:request|prompt|input|messages?)\s+(?:is\s+|are\s+)?too\s+(?:large|long)\s+for\s+(?:(?:this|the)\s+)?(?:model(?:'s)?\s+)?context(?:\s+window)?\b""", RegexOptions.IgnoreCase)]
    private static partial Regex TooLargeForContext();

    [GeneratedRegex("""\b(?:input|prompt|request)\s+(?:is\s+)?too\s+(?:long|large)\s+for\s+(?:this|the)\s+model\b""", RegexOptions.IgnoreCase)]
    private static partial Regex TooLongForModel();

    [GeneratedRegex("""\b(?:input|prompt|request|messages?)\b.{0,40}\b(?:exceed(?:s|ed)?|overflows?|is\s+larger\s+than)\b.{0,40}\b(?:the\s+)?(?:model(?:'s)?\s+)?context(?:\s+(?:length|window))?\b""", RegexOptions.IgnoreCase)]
    private static partial Regex ExceedsModelContext();

    public static bool IsContextWindowExceededError(string detail)
        => StructuredContextOverflow().IsMatch(detail)
           || MaximumContextLength().IsMatch(detail)
           || TooLargeForContext().IsMatch(detail)
           || TooLongForModel().IsMatch(detail)
           || ExceedsModelContext().IsMatch(detail);

    [GeneratedRegex("""\binsufficient[\s_-]+(?:quota|balance|credits?)\b""", RegexOptions.IgnoreCase)]
    private static partial Regex InsufficientQuota();

    [GeneratedRegex("""\b(?:quota|usage[\s_-]+limit)[\s_-]+(?:exceeded|exhausted|reached)\b""", RegexOptions.IgnoreCase)]
    private static partial Regex QuotaExceeded();

    [GeneratedRegex("""\bexceed(?:ed|s)?[\s_-]+(?:(?:your|the)[\s_-]+)?(?:current[\s_-]+)?quota\b""", RegexOptions.IgnoreCase)]
    private static partial Regex ExceedCurrentQuota();

    [GeneratedRegex("""\b(?:balance|credits?)[\s_-]+(?:exhausted|depleted)\b""", RegexOptions.IgnoreCase)]
    private static partial Regex BalanceExhausted();

    [GeneratedRegex("""\bout[\s_-]+of[\s_-]+(?:credits?|budget)\b""", RegexOptions.IgnoreCase)]
    private static partial Regex OutOfCredits();

    public static bool IsQuotaExceededError(string detail)
        => InsufficientQuota().IsMatch(detail)
           || QuotaExceeded().IsMatch(detail)
           || ExceedCurrentQuota().IsMatch(detail)
           || BalanceExhausted().IsMatch(detail)
           || OutOfCredits().IsMatch(detail);

    public static string ErrorChain(object? value)
    {
        var path = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Render(value);

        string Render(object? current)
        {
            switch (current)
            {
                case null:
                    return "<null>";
                case Exception exception:
                {
                    if (!path.Add(exception))
                        return "<circular cause>";
                    try
                    {
                        var message = exception.Message == "" ? exception.GetType().Name : exception.Message;
                        var members = exception is AggregateErrorException { InnerExceptions.Count: > 0 } aggregate
                            ? $" [{string.Join("; ", aggregate.InnerExceptions.Select(Render))}]"
                            : "";
                        var causeText = exception.InnerException is { } inner ? Render(inner) : "";
                        var cause = causeText == "" || causeText == message ? "" : $": {causeText}";
                        return $"{message}{members}{cause}";
                    }
                    finally
                    {
                        path.Remove(exception);
                    }
                }
                default:
                    return current.ToString() ?? "<unrenderable value>";
            }
        }
    }
}

public sealed class AggregateErrorException : Exception
{
    public AggregateErrorException(string message, IReadOnlyList<Exception> innerExceptions)
        : base(message, innerExceptions.FirstOrDefault())
    {
        InnerExceptions = innerExceptions;
    }

    public IReadOnlyList<Exception> InnerExceptions { get; }
}
