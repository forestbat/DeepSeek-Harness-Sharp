using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Dsh.Core;
using Dsh.Runtime;
using Dsh.Plugins;
using Dsh.Transport;
using Dsh.Llm;
using Dsh.Runtime.Events;

namespace Dsh.Boot;

/** The host owns operation identity; workers cannot select tool, session or call IDs. */
public sealed class SafetyBoundary : Service, ISafetyBoundary
{
    private readonly Func<HarnessSettings> _read;
    public SafetyBoundary(Context context, Func<HarnessSettings> read) : base(context, ISafetyBoundary.ServiceName)
        => _read = read;

    private (PermissionRules Rules, string Namespace) Policy(ToolRunContext? run, bool scriptPermissions = false)
    {
        var active = _read();
        return scriptPermissions || run?.UsesScriptPermissions == true
            ? (active.Scripts?.Permissions ?? new ScriptPermissions(), "scripts.permissions")
            : (active.Safety ?? new SafetySettings(), "safety");
    }

    private static PermissionInput Input(ToolRunContext? run, string? path = null, string? intent = null)
        => new(run?.Name, run?.Arguments ?? default,
            run?.Agent?.Session.WorkingDirectory ?? Environment.CurrentDirectory,
            path is null ? null : Dsh.Transport.FilePathAccess.Canonicalize(path), intent,
            PathIsCanonical: path is not null);

    private PermissionDecision Decide(PermissionRules rules, PermissionInput input)
        => PermissionEngine.Decide(rules, input, static path => Dsh.Transport.FilePathAccess.Canonicalize(path));

    public async Task AdmitAsync(ToolRunContext run, bool mediatedFiles, Func<JsonElement, IEnumerable<string>>? filePaths = null)
    {
        var (rules, space) = Policy(run);
        var input = Input(run);
        var decision = Decide(rules, input);
        Audit(run, input, decision, space);
        Reject(decision, space);
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
                var preliminary = Input(run, Path.GetFullPath(value, input.WorkingDirectory), run.InitialFileIntent);
                var known = Decide(rules, preliminary);
                if (known.Decision == "deny")
                {
                    Audit(run, preliminary, known, space);
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
        bool scriptPermissions = false)
    {
        var (rules, space) = Policy(run, scriptPermissions);
        var input = Input(run);
        var decision = Decide(rules, input);
        Audit(run, input, decision, space, lifecycleOwner);
        Reject(decision, space);
        var authorization = await ApproveAsync(run, input, decision, space, Revision(rules), signal,
            alreadyApproved: run?.SafetyApprovalRevision == AuthorizationRevision(rules, space), lifecycleOwner: lifecycleOwner).ConfigureAwait(false);
        if (run is not null) run.SafetyApprovalRevision = authorization;
    }

    public void AuthorizeConfiguredMcp(string lifecycleOwner, CancellationToken signal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleOwner);
        signal.ThrowIfCancellationRequested();
        var (rules, space) = Policy(null);
        var input = Input(null);
        var decision = Decide(rules, input);
        Audit(null, input, decision, space, lifecycleOwner,
            decision.Decision == "deny" ? "rejected" : "configured-lifecycle-authorized");
        Reject(decision, space);
        // The operator's configured connection authorizes discovery only, never a later model call.
    }

    public async Task AuthorizeFileAsync(ToolRunContext? run, string path, string intent,
        CancellationToken signal, string? lifecycleOwner = null, bool settingsReview = false)
    {
        var canonical = Dsh.Transport.FilePathAccess.Canonicalize(path);
        var (rules, space) = Policy(run);
        var input = Input(run, canonical, intent);
        var decision = Decide(rules, input);
        Audit(run, input, decision, space, lifecycleOwner);
        Reject(decision, space);
        // Proposal reads still obey denials; the one settings gate owns the diff notification.
        if (settingsReview && run is not null
            && Ctx.Get<ISettingsMutationGate>(ISettingsMutationGate.ServiceName, false)?.Owns(canonical) == true)
            return;
        var authorization = AuthorizationRevision(rules, space);
        var approved = run?.SafetyApprovalRevision == authorization
            || run?.HasFileApproval(authorization, canonical, intent) == true;
        await ApproveAsync(run, input, decision, space, Revision(rules), signal, alreadyApproved: approved,
            lifecycleOwner: lifecycleOwner).ConfigureAwait(false);
        if (!approved && run is not null && decision.Decision == "notify")
            run.RememberFileApproval(authorization, canonical, intent);
    }

    public void DemandFileAuthorization(ToolRunContext run, string path, string intent)
    {
        run.Signal.ThrowIfCancellationRequested();
        var (rules, space) = Policy(run);
        var input = Input(run, path, intent);
        var decision = Decide(rules, input);
        Audit(run, input, decision, space);
        Reject(decision, space);
        _ = DemandSessionPolicy(run, input, decision, space);
        var revision = AuthorizationRevision(rules, space);
        if (decision.Decision != "allow" && run.SafetyApprovalRevision != revision
            && !run.HasFileApproval(revision, input.Path!, intent))
            throw new UnauthorizedAccessException("The file policy changed before publication; a new authorization is required.");
    }

    private IApprovalService? DemandSessionPolicy(ToolRunContext? run, PermissionInput input,
        PermissionDecision decision, string space)
    {
        var approval = Ctx.Get<IApprovalService>("approval", false);
        if (run?.Agent is { } agent && approval is IApprovalConstraints constraints && constraints.IsDenied(agent))
        {
            Audit(run, input, decision, space, outcome: "rejected");
            throw new UnauthorizedAccessException("The session explicitly denies approval-requiring operations.");
        }
        return approval;
    }

    private async Task<string> ApproveAsync(ToolRunContext? run, PermissionInput input, PermissionDecision decision,
        string space, string revision, CancellationToken signal, bool alreadyApproved = false, string? lifecycleOwner = null)
    {
        signal.ThrowIfCancellationRequested();
        var approval = DemandSessionPolicy(run, input, decision, space);
        if (decision.Decision != "allow" && !alreadyApproved)
        {
            if (run?.Agent is null || approval is null)
                throw new UnauthorizedAccessException("The operation requires approval with an active session and call; lifecycle operations cannot fabricate invocation context.");
            var outcome = await approval.Request(new(run.Agent, run.Name, run.CallId,
                input.Path is null ? $"{space}: approve this entire tool call, including its actual file operations." : $"{space}: approve only {input.Intent} {input.Path}",
                run.Arguments.GetRawText()), signal).ConfigureAwait(false);
            var name = outcome switch
            {
                ApprovalOutcome.AllowedOnce or ApprovalOutcome.AllowedForSession => "approved",
                ApprovalOutcome.Cancelled => "cancelled",
                _ => "rejected",
            };
            Audit(run, input, decision, space, lifecycleOwner, name);
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
            Audit(run, input, latest, currentSpace, lifecycleOwner, "policy-changed");
        Reject(latest, currentSpace);
        _ = DemandSessionPolicy(run, input, latest, currentSpace);
        if (currentRevision != revision && latest.Decision != "allow")
            throw new UnauthorizedAccessException("The policy changed while approval was pending; a new authorization is required.");
        return AuthorizationRevision(current, currentSpace);
    }

    internal static string AuthorizationRevision(PermissionRules rules, string space) => $"{space}:{Revision(rules)}";

    private static void Reject(PermissionDecision decision, string space)
    {
        if (decision.Decision == "deny")
            throw new UnauthorizedAccessException($"Blocked by {space}.blacklist rule \"{decision.Match!.RuleId}\".");
    }

    private void Audit(ToolRunContext? run, PermissionInput input, PermissionDecision decision, string space,
        string? owner = null, string? outcome = null)
        => RecordDecision(Ctx, run, input, decision, space, owner, outcome);

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
