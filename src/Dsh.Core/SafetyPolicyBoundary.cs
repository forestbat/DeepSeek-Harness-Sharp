using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Dsh.Runtime;
using Dsh.Plugins;
using Dsh.Llm;
using Dsh.Runtime.Events;
using Dsh.Boot;

namespace Dsh.Core;

public readonly record struct SettingsApproval(ApprovalRequest? Request = null);

/** The host owns operation identity; workers cannot select tool, session or call IDs. */
public sealed class SafetyBoundary : Service
{
    public const string ServiceName = "safetyBoundary";
    public const string SafetyNamespace = "safety";
    public const string ScriptsNamespace = "scripts.permissions";
    private readonly Func<HarnessSettings> _read;
    private SafetyBoundary(SafetyPolicySource source) : base(source.Ctx, ServiceName)
        => _read = source.Read;

    public static SafetyBoundary? Resolve(Context? context)
    {
        var source = context?.Get<SafetyPolicySource>(SafetyPolicySource.ServiceName, false);
        if (source is null) return null;
        // ServiceTable owns identity; serialize its get/register pair on the registered source.
        lock (source)
            return source.Ctx.Get<SafetyBoundary>(ServiceName, false) ?? new SafetyBoundary(source);
    }

    private PermissionRules Policy(string space)
    {
        var settings = _read();
        return space == ScriptsNamespace
            ? settings.Scripts?.Permissions ?? new ScriptPermissions()
            : settings.Safety ?? new SafetySettings();
    }

    private static PermissionInput Input(ToolRunContext? run, string? canonicalPath = null, string? intent = null)
        => new(run?.Name, run?.Arguments ?? default,
            run?.Agent?.Session.WorkingDirectory ?? Environment.CurrentDirectory,
            canonicalPath, intent, PathIsCanonical: canonicalPath is not null);

    private PermissionDecision Decide(PermissionRules rules, PermissionInput input)
        => PermissionEngine.Decide(rules, input, static path => Dsh.Runtime.CanonicalFilePath.Canonicalize(path));

    private (PermissionRules Rules, PermissionInput Input, PermissionDecision Decision) DecideAndRecord(
        string space, ToolRunContext? run, string? path = null, string? intent = null, string? lifecycleOwner = null)
    {
        var rules = Policy(space);
        var input = Input(run, path, intent);
        var decision = Decide(rules, input);
        RecordDecision(Ctx, run, input, decision, space, lifecycleOwner);
        Reject(decision, space);
        return (rules, input, decision);
    }

    public async Task AdmitAsync(ToolRunContext run, bool mediatedFiles)
    {
        var space = run.PermissionNamespace;
        var (rules, input, decision) = DecideAndRecord(space, run);
        if (mediatedFiles && run.Arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in run.Arguments.EnumerateObject())
            {
                if (property.Name is "path" or "file_path")
                    DemandPreliminary(property.Value);
                else if (property.Name == "files" && property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in property.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("path", out var path))
                            DemandPreliminary(path);
            }

            void DemandPreliminary(JsonElement path)
            {
                if (path.ValueKind != JsonValueKind.String) return;
                var value = path.GetString();
                if (string.IsNullOrWhiteSpace(value)) return;
                var preliminary = Input(run, Dsh.Runtime.CanonicalFilePath.Canonicalize(Path.GetFullPath(value, input.WorkingDirectory)));
                var known = Decide(rules, preliminary);
                if (known.Decision == "deny")
                {
                    RecordDecision(Ctx, run, preliminary, known, space);
                    Reject(known, space);
                }
            }
        }
        // File tools defer a default notification until the real operation and its intent are known.
        if (decision.Decision == "notify" && mediatedFiles)
        {
            _ = DemandSessionPolicy(run, input, decision, space);
            return;
        }
        run.SafetyApprovalRevision = await ApproveAsync(run, input, decision, space, Revision(rules), run.Signal).ConfigureAwait(false);
    }

    public static Task AuthorizeInvocationAsync(Context? context, ToolRunContext? run, bool mediatesFileAccess,
        CancellationToken signal)
        => run is not null && !mediatesFileAccess && Resolve(context) is { } safety
            ? safety.AuthorizeOperationAsync(run.PermissionNamespace, run, signal)
            : Task.CompletedTask;

    public async Task AuthorizeOperationAsync(string space, ToolRunContext? run, CancellationToken signal,
        string? lifecycleOwner = null)
    {
        var (rules, input, decision) = DecideAndRecord(space, run, lifecycleOwner: lifecycleOwner);
        var revision = Revision(rules);
        var authorization = await ApproveAsync(run, input, decision, space, revision, signal,
            alreadyApproved: run?.SafetyApprovalRevision == $"{space}:{revision}", lifecycleOwner: lifecycleOwner).ConfigureAwait(false);
        if (run is not null) run.SafetyApprovalRevision = authorization;
    }

    public void AuthorizeConfiguredLifecycle(string space, string lifecycleOwner, CancellationToken signal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleOwner);
        signal.ThrowIfCancellationRequested();
        var input = Input(null);
        var decision = Decide(Policy(space), input);
        RecordDecision(Ctx, null, input, decision, space, lifecycleOwner,
            decision.Decision == "deny" ? "rejected" : "configured-lifecycle-authorized");
        Reject(decision, space);
    }

    public async Task AuthorizeFileAsync(string space, ToolRunContext? run, string path, string intent,
        CancellationToken signal, string? lifecycleOwner = null, SettingsApproval? settings = null)
    {
        signal.ThrowIfCancellationRequested();
        var canonical = Dsh.Runtime.CanonicalFilePath.Canonicalize(path);
        var (rules, input, decision) = DecideAndRecord(space, run, canonical, intent, lifecycleOwner);
        // Only active-settings proposal reads defer notification to the diff approval.
        if (settings is { Request: null } && intent == "read" && run is not null
            && Ctx.Get<Func<SettingsMutationRequest, Func<string, CancellationToken, Task>, CancellationToken, Task>>(SettingsMutation.ServiceName, false) is not null
            && Ctx.GetProp("harnessOptions") is HarnessOptions options
            && Dsh.Runtime.CanonicalFilePath.SameFile(canonical, options.Home.SettingsFile))
        {
            _ = DemandSessionPolicy(run, input, decision, space);
            return;
        }
        var revision = Revision(rules);
        var approved = settings?.Request is null && run?.HasFileApproval($"{space}:{revision}", canonical, intent) == true;
        var authorization = await ApproveAsync(run, input, decision, space, revision, signal, alreadyApproved: approved,
            lifecycleOwner: lifecycleOwner, request: settings?.Request).ConfigureAwait(false);
        if (!approved && run is not null && decision.Decision == "notify")
            run.RememberFileApproval(authorization, canonical, intent);
    }

    public void RevalidateFile(string space, ToolRunContext? run, string path, string intent, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        var rules = Policy(space);
        var input = Input(run, Dsh.Runtime.CanonicalFilePath.Canonicalize(path), intent);
        var decision = Decide(rules, input);
        if (decision.Decision == "deny")
        {
            RecordDecision(Ctx, run, input, decision, space, outcome: "policy-changed");
            Reject(decision, space);
        }
        _ = DemandSessionPolicy(run, input, decision, space);
        if (decision.Decision != "allow" && run?.HasFileApproval($"{space}:{Revision(rules)}", input.Path!, intent) != true)
        {
            RecordDecision(Ctx, run, input, decision, space, outcome: "policy-changed");
            throw new UnauthorizedAccessException("The file policy changed before publication; a new authorization is required.");
        }
        signal.ThrowIfCancellationRequested();
    }

    private IApprovalService? DemandSessionPolicy(ToolRunContext? run, PermissionInput input,
        PermissionDecision decision, string space)
    {
        var approval = Ctx.Get<IApprovalService>("approval", false);
        if (run?.Agent is { } agent && approval?.IsDenied(agent) == true)
        {
            RecordDecision(Ctx, run, input, decision, space, outcome: "rejected");
            throw new UnauthorizedAccessException("The session explicitly denies approval-requiring operations.");
        }
        return approval;
    }

    private async Task<string> ApproveAsync(ToolRunContext? run, PermissionInput input, PermissionDecision decision,
        string space, string revision, CancellationToken signal, bool alreadyApproved = false, string? lifecycleOwner = null,
        ApprovalRequest? request = null)
    {
        signal.ThrowIfCancellationRequested();
        var approval = DemandSessionPolicy(run, input, decision, space);
        if (decision.Decision != "allow" && !alreadyApproved)
        {
            if (run?.Agent is null || approval is null)
                throw new UnauthorizedAccessException("The operation requires approval with an active session and call; lifecycle operations cannot fabricate invocation context.");
            var outcome = await approval.Request(request ?? new(run.Agent, run.Name, run.CallId,
                input.Path is null ? $"{space}: approve this entire tool call, including its actual file operations." : $"{space}: approve only {input.Intent} {input.Path}",
                run.Arguments.GetRawText()), signal).ConfigureAwait(false);
            var name = outcome switch
            {
                ApprovalOutcome.AllowedOnce or ApprovalOutcome.AllowedForSession => "approved",
                ApprovalOutcome.Cancelled => "cancelled",
                _ => "rejected",
            };
            RecordDecision(Ctx, run, input, decision, space, lifecycleOwner, name);
            if (outcome == ApprovalOutcome.Cancelled)
                throw new OperationCanceledException("The permission request was cancelled.", signal);
            if (name != "approved")
                throw new UnauthorizedAccessException($"The operation was not approved ({outcome}).");
        }
        signal.ThrowIfCancellationRequested();
        var current = Policy(space);
        var latest = Decide(current, input);
        var currentRevision = Revision(current);
        if (latest.Decision == "deny" || currentRevision != revision)
            RecordDecision(Ctx, run, input, latest, space, lifecycleOwner, "policy-changed");
        Reject(latest, space);
        _ = DemandSessionPolicy(run, input, latest, space);
        if (currentRevision != revision && latest.Decision != "allow")
            throw new UnauthorizedAccessException("The policy changed while approval was pending; a new authorization is required.");
        return $"{space}:{currentRevision}";
    }

    private static void Reject(PermissionDecision decision, string space)
    {
        if (decision.Decision == "deny")
            throw new UnauthorizedAccessException($"Blocked by {space}.blacklist rule \"{decision.Match!.RuleId}\".");
    }

    public static void RecordDecision(Context context, ToolRunContext? run, PermissionInput input,
        PermissionDecision decision, string space, string? owner = null, string? outcome = null)
    {
        var fields = decision.Match?.Fields ?? [];
        var matchedFields = fields.Count == 0 ? Array.Empty<string>() : new string[fields.Count];
        var matchedPatterns = fields.Count == 0 ? Array.Empty<string>() : new string[fields.Count];
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            matchedFields[index] = field.Field;
            matchedPatterns[index] = field.Pattern;
        }
        var payload = new SafetyDecisionAuditPayload(decision.Match?.RuleId, decision.Match?.RuleKind ?? "default",
            decision.AutoApprove, decision.Decision, run?.Name, PermissionEngine.DialectName(PermissionEngine.DialectOf(run?.Name)),
            matchedFields, matchedPatterns, input.Path, input.Intent, run?.Agent?.Session.Id.Value,
            run?.CallId.Value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), space, owner, outcome);
        if (run?.Agent is { } agent) agent.Session.Append(payload);
        else
        {
            context.LoggerFor("safety").Info("%s", DshJson.Serialize(payload));
            context.Emit(new SafetyDecisionNotification(payload));
        }
    }

    public static string Revision(PermissionRules rules)
    {
        var text = new StringBuilder(rules.AutoApprove ? "1" : "0");
        Add(rules.Blacklist, "deny");
        Add(rules.Whitelist, "allow");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        void Value(string? value) => text.Append(value?.Length ?? -1).Append(':').Append(value);
        void Add(Dictionary<string, PermissionMatcher> map, string kind)
        {
            Value(kind);
            foreach (var (id, matcher) in map)
            {
                Value(id); Value(matcher.Dialect); Value(matcher.Path); Value(matcher.Intent);
                foreach (var tool in matcher.Tool ?? []) Value(tool);
                Value(null);
                foreach (var (key, value) in matcher.Args) { Value(key); Value(value); }
                Value(null);
            }
        }
    }

}

public sealed record SafetyDecisionNotification(SafetyDecisionAuditPayload Decision) : INotification
{
    public static string EventName => "safety/decision";
}
