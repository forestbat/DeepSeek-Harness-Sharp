using Cordis;
using Dsh.Core;

namespace Dsh.Jobs;

public abstract class JobsService : Service
{
    public const string ServiceName = "jobs";

    protected JobsService(Context ctx) : base(ctx, ServiceName)
    {
    }

    public abstract string Start(JobStart spec);

    public abstract IReadOnlyList<JobSnapshot> List(IAgent? caller = null);

    public abstract JobSnapshot Get(string id, IAgent? caller = null);

    public abstract JobRead Read(string id, IAgent? caller = null);

    public abstract JobKillOutcome Kill(string id, IAgent? caller = null, string? reason = null);

    public abstract Task<JobSnapshot> WaitAsync(string id, double timeoutMs, IAgent? caller = null, CancellationToken signal = default);

    public abstract IDisposable OnJobDone(JobDoneListener listener);

    public abstract IDisposable OnJobsChanged(JobsChangedListener listener);

    public abstract IDisposable AttachController(string name);
}
