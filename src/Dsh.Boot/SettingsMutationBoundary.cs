using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Boot;

/** Registered before plugin composition, so removing the interaction plugin never removes settings enforcement. */
public sealed class SettingsMutationBoundary(Context context, HarnessHome home) : Service(context, ISettingsMutationGate.ServiceName), ISettingsMutationGate
{
    private ISettingsMutationGate? _reviewer;
    public string SettingsPath => Dsh.Transport.PinnedFileAccess.Canonicalize(home.SettingsFile);
    public bool Owns(string path) => SettingsFilePath.SameFile(path, home.SettingsFile);

    public IDisposable InstallReviewer(ISettingsMutationGate reviewer)
    {
        if (Interlocked.CompareExchange(ref _reviewer, reviewer, null) is not null)
            throw new InvalidOperationException("A settings mutation reviewer is already installed.");
        return new ReviewerLease(this, reviewer);
    }

    public Task CommitAsync(SettingsMutationRequest request, Func<CancellationToken, Task> commit, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _reviewer) is { } reviewer)
            return reviewer.CommitAsync(request, commit, cancellationToken);
        var (before, after) = SettingsMutationDiff.Redact(request.Before, request.After);
        var run = request.Context;
        run.RecordSettingsMutation(new(Guid.NewGuid().ToString("N"), run.CallId, run.RootCallIdValue, run.Name,
            SettingsPath, before, after, "unavailable"), Ctx);
        throw new InvalidOperationException("Settings mutations require the active interaction approval service.");
    }

    private sealed class ReviewerLease(SettingsMutationBoundary owner, ISettingsMutationGate reviewer) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref owner._reviewer, null, reviewer);
    }
}
