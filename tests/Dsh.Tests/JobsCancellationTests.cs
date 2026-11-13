using Dsh.Jobs;
using Dsh.Runtime;

namespace Dsh.Tests;

public class JobsCancellationTests
{
    [Theory]
    [InlineData(JobStatus.Killed)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    public void Kill_PreservesAProducerThatSettlesDuringCancellation(JobStatus terminalStatus)
    {
        var ctx = new Context();
        var jobs = new LocalJobsService(ctx);
        using var controller = jobs.AttachController("cancellation-tests");
        var completion = new TaskCompletionSource<JobOutcome>();
        var outcome = new JobOutcome(terminalStatus, "producer settled during cancellation", "final output");
        var id = jobs.Start(new JobStart
        {
            Kind = "operation",
            Label = "Synchronously cancellable operation",
            Run = () => new JobHooks(_ => completion.SetResult(outcome), completion.Task),
        });

        Assert.Equal(JobKillOutcome.Requested, jobs.Kill(id));

        var snapshot = jobs.Get(id);
        Assert.Equal(terminalStatus, snapshot.Status);
        Assert.NotNull(snapshot.FinishedAt);
        Assert.Equal(outcome.Detail, snapshot.Detail);
        Assert.Equal(outcome.Output, jobs.Read(id).Text);
        Assert.Equal(JobKillOutcome.AlreadyFinished, jobs.Kill(id));
    }

    [Fact]
    public void Kill_ReleasesTheSlotWhenTheProducerSettlesDuringCancellation()
    {
        var ctx = new Context();
        var jobs = new LocalJobsService(ctx, new LocalJobsConfig { MaxConcurrentJobsPerOwner = 1 });
        using var controller = jobs.AttachController("cancellation-tests");
        var completion = new TaskCompletionSource<JobOutcome>();
        var first = jobs.Start(new JobStart
        {
            Kind = "operation",
            Label = "First operation",
            Run = () => new JobHooks(_ => completion.SetResult(new JobOutcome(JobStatus.Killed)), completion.Task),
        });
        jobs.Kill(first);

        var second = jobs.Start(new JobStart
        {
            Kind = "operation",
            Label = "Next operation",
            Run = () => new JobHooks(_ => throw new InvalidOperationException("The completed operation cannot be cancelled."),
                Task.FromResult(new JobOutcome(JobStatus.Completed))),
        });

        Assert.Equal(JobStatus.Completed, jobs.Get(second).Status);
    }
}
