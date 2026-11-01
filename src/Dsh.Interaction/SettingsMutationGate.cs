using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Interaction;

/** One approval and publication boundary for trusted tools and the isolated capability broker. */
public sealed class SettingsMutationGate(Context context, HarnessHome home) : ISettingsMutationGate
{
    public string SettingsPath => Dsh.Transport.PinnedFileAccess.Canonicalize(home.SettingsFile);
    public bool Owns(string path) => SettingsFilePath.SameFile(path, home.SettingsFile);

    public async Task CommitAsync(SettingsMutationRequest request, Func<CancellationToken, Task> commit, CancellationToken cancellationToken = default)
    {
        var run = request.Context;
        var id = Guid.NewGuid().ToString("N");
        var target = SettingsPath;
        var before = "[Proposal unavailable]";
        var after = "[Proposal unavailable]";
        var outcome = "failed";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Signal);
        try
        {
            (before, after) = SettingsMutationDiff.Redact(request.Before, request.After);
            if (!SettingsMutationLimits.CanReview(request.Before) || !SettingsMutationLimits.CanReview(request.After))
                throw new InvalidOperationException("The settings document exceeds the 4 MiB review limit.");
            linked.Token.ThrowIfCancellationRequested();
            if (!Owns(request.Path))
                throw new InvalidOperationException("The mutation does not target the active settings document.");
            if (run.Agent is null)
                throw new InvalidOperationException("Settings mutations require an active model session.");
            var approval = context.Get<IApprovalService>("approval", false)
                ?? throw new InvalidOperationException("Settings mutations require the active approval service.");
            var safety = HarnessSettings.LoadForReview(home).Safety;
            if (SafetyCommandGuard.MatchingRule(safety?.Blacklist, run.Name, run.Arguments) is { } denied)
            {
                outcome = "rejected";
                throw new UnauthorizedAccessException($"Settings mutation blocked by safety.blacklist rule \"{denied}\".");
            }
            var arguments = new JsonObject { ["settingsMutation"] = true, ["path"] = target, ["before"] = before, ["after"] = after };
            var decision = await approval.Request(new(run.Agent, run.Name, run.CallId,
                "Change the active settings file. Review the complete redacted before/after diff; this affects all sessions.", arguments.ToJsonString(),
                AllowByRule: safety?.AutoApprove == true || SafetyCommandGuard.MatchingRule(safety?.Whitelist, run.Name, run.Arguments) is not null), linked.Token).ConfigureAwait(false);
            if (decision is not (ApprovalOutcome.AllowedOnce or ApprovalOutcome.AllowedForSession))
            {
                outcome = decision == ApprovalOutcome.Cancelled ? "cancelled" : "rejected";
                if (decision == ApprovalOutcome.Cancelled)
                    throw new OperationCanceledException("Settings mutation was cancelled.", linked.Token);
                throw new UnauthorizedAccessException($"Settings mutation was not approved ({decision}).");
            }
            await HarnessSettings.CommitApprovedMutationAsync(target, request.Before, request.After, signal =>
            {
                if (target != SettingsPath || !Owns(request.Path))
                    throw new InvalidOperationException("The active settings path changed while approval was pending.");
                signal.ThrowIfCancellationRequested();
                return commit(signal);
            }, linked.Token).ConfigureAwait(false);
            outcome = "committed";
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            run.RecordSettingsMutation(new(id, run.CallId, run.RootCallIdValue, run.Name, target, before, after, outcome), context);
        }
    }

}
