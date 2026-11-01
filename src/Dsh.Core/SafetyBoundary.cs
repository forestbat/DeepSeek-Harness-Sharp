using System.Text;
using System.Text.Json;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Core;

public interface ISafetyBoundary
{
    const string ServiceName = "safetyBoundary";
    Task AdmitAsync(ToolRunContext run, bool mediatedFiles);
    Task<Action<PinnedFileTarget>> AuthorizeFileAsync(ToolRunContext? run, string path, string intent,
        CancellationToken signal, string? lifecycleOwner = null, bool settingsReview = false);
    Task AuthorizeOperationAsync(ToolRunContext? run, CancellationToken signal, string? lifecycleOwner = null,
        bool scriptPermissions = false);
    void AuthorizeConfiguredMcp(string lifecycleOwner, CancellationToken signal);
}

public sealed record SafetyDecisionAuditPayload(string? RuleId, string RuleKind, bool AutoApprove,
    string Decision, string? Tool, string? Dialect, string? MatchedField, string? MatchedPattern,
    string? Path, string? Intent, string? SessionId, string? CallId, long Timestamp,
    string Namespace = "safety", string? LifecycleOwner = null, string? Outcome = null) : SessionEventPayload
{
    public const string EventType = "safety/decision";
    public override string Type => EventType;
}

/** Every actual file is authorized, then opened through the existing pinned-object boundary. */
public static class SafetyFileAccess
{
    public static async Task<Action<PinnedFileTarget>> AuthorizeAsync(string path, string intent,
        ToolRunContext run, CancellationToken signal, bool settingsReview = false)
        => SettingsMutationScope.Current?.Safety is { } safety
            ? await safety.AuthorizeFileAsync(run, path, intent, signal, settingsReview: settingsReview).ConfigureAwait(false)
            : static _ => { };

    public static async Task<PinnedFileAccess> OpenReadAsync(string path, ToolRunContext run, bool settingsReview = false)
    {
        var authorize = await AuthorizeAsync(path, "read", run, run.Signal, settingsReview).ConfigureAwait(false);
        return PinnedFileAccess.Open(path, PinnedFileMode.Read, authorize);
    }

    public static async Task<IReadOnlyList<(string Path, bool IsDirectory)>> ReadDirectoryAsync(string path, ToolRunContext run)
    {
        var authorize = await AuthorizeAsync(path, "read", run, run.Signal).ConfigureAwait(false);
        return await PinnedFileAccess.ReadDirectoryAsync(path, authorize,
            entry => AuthorizeAsync(entry, "read", run, run.Signal)).ConfigureAwait(false);
    }

    public static async Task<string> ReadTextAsync(string path, ToolRunContext run)
    {
        await using var file = await OpenReadAsync(path, run).ConfigureAwait(false);
        using var reader = new StreamReader(file.Stream, new UTF8Encoding(false, false));
        return await reader.ReadToEndAsync(run.Signal).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadBytesAsync(string path, ToolRunContext run, long maximum)
    {
        await using var file = await OpenReadAsync(path, run).ConfigureAwait(false);
        if (file.Stream.Length > maximum)
            throw new IOException("The file exceeds the permitted read size.");
        var bytes = new byte[checked((int)file.Stream.Length)];
        await file.Stream.ReadExactlyAsync(bytes, run.Signal).ConfigureAwait(false);
        return bytes;
    }
}
