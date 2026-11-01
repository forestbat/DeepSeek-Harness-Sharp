using Dsh.Runtime;
using Dsh.Core;
using Dsh.Runtime.Events;

namespace Dsh.Boot;

public sealed record SafetyPolicyActivationStatus(bool Rejected, string? Reason);
public sealed record SafetyPolicyStateChanged(SafetyPolicyActivationStatus Status) : INotification
{
    public static string EventName => "safety/policy-state";
}
public sealed record SafetyPolicyRejected(string Reason) : INotification
{
    public static string EventName => "safety/policy-rejected";
}

/** Runtime-loaded full-host artifacts expand the startup TCB and retain BCL/native authority until process exit. */
public sealed class SettingsPolicyActivation : IDisposable
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, List<SettingsPolicyActivation>> Owners = new(StringComparer.Ordinal);
    private readonly Context _context;
    private readonly string _path;
    private string _accepted;
    private string? _pending;
    private static bool FullHostAuthority;
    private bool _disposed;
    private string? _rejectedRevision;
    public SafetyPolicyActivationStatus Status { get; private set; } = new(false, null);
    private const string Unsupported = "The new policy is not activated: an externally loaded full-host artifact has already expanded process authority and cannot enforce per-file rules. Restart without that artifact or use mediated/isolated execution before enabling this policy.";

    public SettingsPolicyActivation(Context context, HarnessHome home, HarnessSettings settings)
    {
        _context = context;
        _path = Dsh.Transport.PinnedFileAccess.Canonicalize(home.SettingsFile);
        _accepted = settings.SnapshotDocument;
        lock (Gate)
        {
            if (FullHostAuthority && NeedsPerFileAuthority(settings.Safety ?? new SafetySettings()))
                throw new NotSupportedException(Unsupported);
            if (!Owners.TryGetValue(_path, out var list)) Owners.Add(_path, list = []);
            list.Add(this);
        }
        context.SetOwn("safetyPolicyActivation", this);
    }

    internal static bool NeedsPerFileAuthority(PermissionRules rules)
        => rules.Blacklist.Values.Any(FileMatcher)
            || !rules.AutoApprove && rules.Whitelist.Values.Any(FileMatcher);
    private static bool FileMatcher(PermissionMatcher matcher) => matcher.Path is not null || matcher.Intent != "any";

    internal static void RegisterFullHost(HarnessHome home)
    {
        _ = HarnessSettings.LoadForReview(home);
        lock (Gate)
        {
            if (FullHostAuthority) return;
            foreach (var owner in Owners.Values.SelectMany(owners => owners))
            {
                var current = HarnessSettings.ParseSnapshot(owner._pending ?? owner._accepted, owner._path);
                if (NeedsPerFileAuthority(current.Safety ?? new SafetySettings()))
                    throw new NotSupportedException("Full-host loading requires per-file authority that this process cannot enforce. No host plugin was started.");
            }
            FullHostAuthority = true;
        }
    }

    internal static HarnessSettings AcceptSnapshot(string path, string text, HarnessSettings candidate)
    {
        List<SettingsPolicyActivation>? rejected = null;
        List<SettingsPolicyActivation>? recovered = null;
        string? accepted = null;
        lock (Gate)
        {
            if (!Owners.TryGetValue(path, out var owners)) return candidate;
            if (FullHostAuthority && NeedsPerFileAuthority(candidate.Safety ?? new SafetySettings()))
            {
                accepted = owners[0]._accepted;
                var revision = SafetyBoundary.Revision(candidate.Safety ?? new SafetySettings());
                foreach (var owner in owners)
                {
                    owner.Status = new(true, Unsupported);
                    if (owner._rejectedRevision == revision) continue;
                    owner._rejectedRevision = revision;
                    (rejected ??= []).Add(owner);
                }
            }
            else
                foreach (var owner in owners)
                {
                    if (owner.Status.Rejected) (recovered ??= []).Add(owner);
                    owner._accepted = text;
                    owner._rejectedRevision = null;
                    owner.Status = new(false, null);
                }
        }
        if (rejected is not null)
            foreach (var owner in rejected)
            {
                owner._context.LoggerFor("safety").Warn("%s", Unsupported);
                owner._context.Emit(new SafetyPolicyRejected(Unsupported));
                owner._context.Emit(new SafetyPolicyStateChanged(owner.Status));
            }
        if (recovered is not null)
            foreach (var owner in recovered) owner._context.Emit(new SafetyPolicyStateChanged(owner.Status));
        return accepted is null ? candidate : HarnessSettings.ParseSnapshot(accepted, path);
    }

    internal static void ValidateCandidate(string path, HarnessSettings candidate)
    {
        lock (Gate)
            if (Owners.ContainsKey(path) && FullHostAuthority
                && NeedsPerFileAuthority(candidate.Safety ?? new SafetySettings()))
                throw new NotSupportedException(Unsupported);
    }

    internal static Publication BeginPublication(string path, string text)
    {
        var candidate = HarnessSettings.ParseSnapshot(text, path);
        lock (Gate)
        {
            ValidateCandidate(path, candidate);
            var owners = Owners.TryGetValue(path, out var active) ? active.ToArray() : [];
            foreach (var owner in owners) owner._pending = text;
            return new Publication(owners, text);
        }
    }

    internal sealed class Publication(SettingsPolicyActivation[] owners, string text) : IDisposable
    {
        public void Commit()
        {
            SettingsPolicyActivation[] recovered;
            lock (Gate)
            {
                recovered = owners.Where(owner => owner.Status.Rejected).ToArray();
                foreach (var owner in owners)
                {
                    owner._accepted = text;
                    owner.Status = new(false, null);
                    owner._rejectedRevision = null;
                }
            }
            foreach (var owner in recovered) owner._context.Emit(new SafetyPolicyStateChanged(owner.Status));
        }
        public void Dispose()
        {
            lock (Gate)
                foreach (var owner in owners) owner._pending = null;
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!Owners.TryGetValue(_path, out var owners)) return;
            owners.Remove(this);
            if (owners.Count == 0) Owners.Remove(_path);
        }
    }
}
