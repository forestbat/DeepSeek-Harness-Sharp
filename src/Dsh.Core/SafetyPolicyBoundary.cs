using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Dsh.Runtime;
using Dsh.Plugins;
using Dsh.Llm;
using Dsh.Runtime.Events;
using Dsh.Boot;

namespace Dsh.Core;

/** The host owns operation identity; workers cannot select tool, session or call IDs. */
public sealed class SafetyBoundary : Service
{
    public const string ServiceName = "safetyBoundary";
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

    private (PermissionRules Rules, string Namespace) Policy(ToolRunContext? run, bool? scriptPermissions = null)
    {
        var scripts = scriptPermissions ?? run?.UsesScriptPermissions == true;
        var settings = _read();
        return (scripts ? settings.Scripts?.Permissions ?? new ScriptPermissions() : settings.Safety ?? new SafetySettings(),
            scripts ? "scripts.permissions" : "safety");
    }

    private static PermissionInput Input(ToolRunContext? run, string? canonicalPath = null, string? intent = null)
        => new(run?.Name, run?.Arguments ?? default,
            run?.Agent?.Session.WorkingDirectory ?? Environment.CurrentDirectory,
            canonicalPath, intent, PathIsCanonical: canonicalPath is not null);

    private PermissionDecision Decide(PermissionRules rules, PermissionInput input)
        => PermissionEngine.Decide(rules, input, static path => Dsh.Runtime.CanonicalFilePath.Canonicalize(path));

    private (PermissionRules Rules, string Namespace, PermissionInput Input, PermissionDecision Decision) DecideAndRecord(
        ToolRunContext? run, string? path = null, string? intent = null, string? lifecycleOwner = null,
        bool? scriptPermissions = null, bool configuredLifecycle = false)
    {
        var (rules, space) = Policy(run, scriptPermissions);
        var input = Input(run, path, intent);
        var decision = Decide(rules, input);
        var outcome = configuredLifecycle
            ? decision.Decision == "deny" ? "rejected" : "configured-lifecycle-authorized"
            : null;
        RecordDecision(Ctx, run, input, decision, space, lifecycleOwner, outcome);
        Reject(decision, space);
        return (rules, space, input, decision);
    }

    public async Task AdmitAsync(ToolRunContext run, bool mediatedFiles, Func<JsonElement, IEnumerable<string>>? filePaths = null)
    {
        var (rules, space, input, decision) = DecideAndRecord(run);
        if (mediatedFiles && run.Arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in run.Arguments.EnumerateObject())
            {
                if (property.Name is not ("path" or "file_path") || property.Value.ValueKind != JsonValueKind.String) continue;
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                DemandPreliminary(value);
            }
            if (filePaths is not null)
                foreach (var path in filePaths(run.Arguments))
                    if (!string.IsNullOrWhiteSpace(path)) DemandPreliminary(path);

            void DemandPreliminary(string value)
            {
                var preliminary = Input(run, Dsh.Runtime.CanonicalFilePath.Canonicalize(Path.GetFullPath(value, input.WorkingDirectory)), run.InitialFileIntent);
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

    public async Task AuthorizeOperationAsync(ToolRunContext? run, CancellationToken signal, string? lifecycleOwner = null,
        bool? scriptPermissions = null, bool configuredLifecycle = false)
    {
        configuredLifecycle &= run is null;
        if (configuredLifecycle)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleOwner);
            signal.ThrowIfCancellationRequested();
        }
        var (rules, space, input, decision) = DecideAndRecord(run, lifecycleOwner: lifecycleOwner,
            scriptPermissions: scriptPermissions, configuredLifecycle: configuredLifecycle);
        if (configuredLifecycle) return;
        var revision = Revision(rules);
        var authorization = await ApproveAsync(run, input, decision, space, revision, signal,
            alreadyApproved: run?.SafetyApprovalRevision == $"{space}:{revision}", lifecycleOwner: lifecycleOwner).ConfigureAwait(false);
        if (run is not null) run.SafetyApprovalRevision = authorization;
    }


    public async Task AuthorizeFileAsync(ToolRunContext? run, string path, string intent,
        CancellationToken signal, string? lifecycleOwner = null, bool settingsReview = false,
        ApprovalRequest? settingsApproval = null, bool canApprove = true)
    {
        var canonical = Dsh.Runtime.CanonicalFilePath.Canonicalize(path);
        var (rules, space, input, decision) = DecideAndRecord(run, canonical, intent, lifecycleOwner,
            scriptPermissions: settingsApproval is not null ? false : null);
        // Proposal reads still obey denials; the one settings gate owns the diff notification.
        if (settingsReview && run is not null
            && Ctx.Get<SettingsMutationGate>(SettingsMutationGate.ServiceName, false)?.Owns(canonical) == true)
            return;
        var revision = Revision(rules);
        var authorization = $"{space}:{revision}";
        var approved = (settingsApproval is null || !canApprove)
            && (run?.SafetyApprovalRevision == authorization
                || run?.HasFileApproval(authorization, canonical, intent) == true);
        await ApproveAsync(run, input, decision, space, revision, signal, alreadyApproved: approved,
            lifecycleOwner: lifecycleOwner, settingsApproval: settingsApproval, canApprove: canApprove).ConfigureAwait(false);
        if (!approved && run is not null && decision.Decision == "notify")
            run.RememberFileApproval(authorization, canonical, intent);
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
        ApprovalRequest? settingsApproval = null, bool canApprove = true)
    {
        signal.ThrowIfCancellationRequested();
        var approval = DemandSessionPolicy(run, input, decision, space);
        if (decision.Decision != "allow" && !alreadyApproved)
        {
            if (!canApprove)
                throw new UnauthorizedAccessException("The file policy changed before publication; a new authorization is required.");
            if (run?.Agent is null || approval is null)
                throw new UnauthorizedAccessException("The operation requires approval with an active session and call; lifecycle operations cannot fabricate invocation context.");
            var outcome = await approval.Request(settingsApproval ?? new(run.Agent, run.Name, run.CallId,
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
        var (current, currentSpace) = Policy(run, scriptPermissions: space == "scripts.permissions");
        var latest = Decide(current, input);
        var currentRevision = Revision(current);
        if (latest.Decision == "deny" || currentRevision != revision)
            RecordDecision(Ctx, run, input, latest, currentSpace, lifecycleOwner, "policy-changed");
        Reject(latest, currentSpace);
        _ = DemandSessionPolicy(run, input, latest, currentSpace);
        if (currentRevision != revision && latest.Decision != "allow")
            throw new UnauthorizedAccessException("The policy changed while approval was pending; a new authorization is required.");
        return $"{currentSpace}:{currentRevision}";
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
        if (fields.Count == 0) Record(null);
        else foreach (var field in fields) Record(field);
        void Record(PermissionField? field)
        {
            var payload = new SafetyDecisionAuditPayload(decision.Match?.RuleId, decision.Match?.RuleKind ?? "default",
                decision.AutoApprove, decision.Decision, run?.Name, PermissionEngine.DialectName(PermissionEngine.DialectOf(run?.Name)),
                field?.Field, field?.Pattern, input.Path, input.Intent, run?.Agent?.Session.Id.Value,
                run?.CallId.Value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), space, owner, outcome);
            if (run?.Agent is { } agent) agent.Session.Append(payload);
            else
            {
                context.LoggerFor("safety").Info("%s", DshJson.Serialize(payload));
                context.Emit(new SafetyDecisionNotification(payload));
            }
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
