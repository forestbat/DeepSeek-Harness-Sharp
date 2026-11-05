using System.Collections.Concurrent;
using Dsh.Sandbox;

namespace Dsh.Tests;

// These callbacks exercise batch coordination only; they do not model or prove Windows ACL access.
public sealed class WindowsReadProofCoordinatorTests
{
    [Theory(Timeout = 10000)]
    [InlineData(3, 3)]
    [InlineData(128, 64)]
    public void Batches_GrowFromOneEntryAndReuseTheSameBoundedDedicatedWorkers(int entries, int expectedWorkers)
    {
        using var proofs = new WindowsReadProofCoordinator(entries);
        using var ready = new Barrier(expectedWorkers);
        var workers = new[] { new ConcurrentDictionary<Thread, byte>(), new ConcurrentDictionary<Thread, byte>() };
        var visits = new int[entries * 2 + 1];
        var cancellationToken = TestContext.Current.CancellationToken;
        Thread? initialWorker = null;
        Assert.Equal(1, proofs.ProbePrefix(0, 1, index =>
        {
            initialWorker = Thread.CurrentThread;
            visits[index]++;
            return true;
        }, cancellationToken));

        for (var batch = 0; batch < workers.Length; batch++)
        {
            var current = batch;
            var start = 1 + batch * entries;
            Assert.Equal(start + entries, proofs.ProbePrefix(start, start + entries, index =>
            {
                Assert.False(Thread.CurrentThread.IsThreadPoolThread);
                workers[current].TryAdd(Thread.CurrentThread, 0);
                Interlocked.Increment(ref visits[index]);
                if (index < start + expectedWorkers)
                    ready.SignalAndWait(cancellationToken);
                return true;
            }, cancellationToken));
        }

        Assert.Equal(expectedWorkers, workers[0].Count);
        Assert.Contains(initialWorker!, workers[0].Keys);
        Assert.True(workers[0].Keys.ToHashSet().SetEquals(workers[1].Keys));
        Assert.All(visits, static count => Assert.Equal(1, count));
        proofs.Dispose();
        Assert.All(workers[0].Keys, static worker => Assert.False(worker.IsAlive));
    }

    [Theory(Timeout = 10000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FalseFailureAndCancellation_DrainBeforeReturningAndNeverInvokeOldCallbacks(int stop)
    {
        using var proofs = new WindowsReadProofCoordinator(2);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var firstStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Read proof callback failed.");
        var workers = new ConcurrentDictionary<Thread, byte>();
        var calls = 0;
        var finished = 0;
        var pending = Task.Run(() =>
        {
            var prefixEnd = -1;
            var error = Record.Exception(() => prefixEnd = proofs.ProbePrefix(0, 128, index =>
            {
                workers.TryAdd(Thread.CurrentThread, 0);
                Interlocked.Increment(ref calls);
                try
                {
                    if (index == 0)
                    {
                        firstStarted.Set();
                        release.Wait(TestContext.Current.CancellationToken);
                    }
                    else if (index == 1)
                    {
                        firstStarted.Wait(TestContext.Current.CancellationToken);
                        stopping.SetResult();
                        if (stop == 0)
                            return false;
                        if (stop == 1)
                            throw failure;
                        cancellation.Cancel();
                    }
                    return true;
                }
                finally { Interlocked.Increment(ref finished); }
            }, cancellation.Token));
            return (PrefixEnd: prefixEnd, Error: error, Finished: Volatile.Read(ref finished));
        }, TestContext.Current.CancellationToken);

        try
        {
            await stopping.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.Set();
            await pending;
        }
        var observed = await pending;
        Assert.True(calls >= 2);
        Assert.Equal(calls, observed.Finished);
        if (stop == 0)
        {
            Assert.Null(observed.Error);
            Assert.Equal(1, observed.PrefixEnd);
            // Re-probe an overlapping suffix with a new callback after the caller regains control.
            // No result from the drained batch may replace a fresh callback invocation.
            var suffixVisits = new int[128];
            Assert.Equal(128, proofs.ProbePrefix(2, 128, index =>
            {
                Assert.Equal(calls, Volatile.Read(ref finished));
                Interlocked.Increment(ref suffixVisits[index]);
                return true;
            }, TestContext.Current.CancellationToken));
            Assert.All(suffixVisits[..2], static count => Assert.Equal(0, count));
            Assert.All(suffixVisits[2..], static count => Assert.Equal(1, count));
        }
        else
        {
            Assert.Equal(-1, observed.PrefixEnd);
            if (stop == 1)
                Assert.Same(failure, observed.Error);
            else
                Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(observed.Error).CancellationToken);
        }
        proofs.Dispose();
        Assert.Equal(observed.Finished, Volatile.Read(ref finished));
        Assert.Equal(calls, Volatile.Read(ref finished));
        Assert.All(workers.Keys, static worker => Assert.False(worker.IsAlive));
    }

    [Fact(Timeout = 10000)]
    public void CompletedBatch_ReleasesItsCancellationRegistrationBeforeReusingWorkers()
    {
        using var proofs = new WindowsReadProofCoordinator(1);
        using var cancellation = new CancellationTokenSource();
        var visits = 0;
        Assert.Equal(1, proofs.ProbePrefix(0, 1, _ =>
        {
            visits++;
            return true;
        }, cancellation.Token));

        cancellation.Cancel();

        Assert.Equal(2, proofs.ProbePrefix(1, 2, static _ => true, TestContext.Current.CancellationToken));
        Assert.Equal(1, visits);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackFailure_RemainsPrimaryOverCancellationAndDrainsOtherWorkers(bool cancelFirst)
    {
        using var proofs = new WindowsReadProofCoordinator(2);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var firstStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var releaseFailure = new ManualResetEventSlim();
        var failing = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("First read proof callback failed.");
        var finished = 0;
        var pending = Task.Run(() => Record.Exception(() => proofs.ProbePrefix(0, 128, index =>
        {
            if (index == 0)
            {
                firstStarted.Set();
                release.Wait(TestContext.Current.CancellationToken);
                Interlocked.Increment(ref finished);
                return true;
            }
            firstStarted.Wait(TestContext.Current.CancellationToken);
            failing.SetResult(Thread.CurrentThread);
            releaseFailure.Wait(TestContext.Current.CancellationToken);
            throw failure;
        }, cancellation.Token)), TestContext.Current.CancellationToken);

        try
        {
            var failedWorker = await failing.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (cancelFirst)
                cancellation.Cancel();
            releaseFailure.Set();
            Assert.True(SpinWait.SpinUntil(() => !failedWorker.IsAlive, TimeSpan.FromSeconds(5)));
            if (!cancelFirst)
                cancellation.Cancel();
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            releaseFailure.Set();
            release.Set();
            await pending;
        }

        Assert.Same(failure, await pending);
        Assert.Equal(1, Volatile.Read(ref finished));
    }

    [Fact(Timeout = 10000)]
    public void PreCanceledBatch_JoinsPreviouslyStartedWorkersWithoutInvokingItsCallback()
    {
        using var proofs = new WindowsReadProofCoordinator(1);
        Thread? worker = null;
        Assert.Equal(1, proofs.ProbePrefix(0, 1, _ =>
        {
            worker = Thread.CurrentThread;
            return true;
        }, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var visits = 0;

        var error = Assert.ThrowsAny<OperationCanceledException>(() => proofs.ProbePrefix(1, 2, _ =>
        {
            visits++;
            return true;
        }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, visits);
        Assert.NotNull(worker);
        Assert.False(worker.IsAlive);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_DrainsAndJoinsEvenWhenItsJoinIsInterrupted(bool interruptJoin)
    {
        using var proofs = new WindowsReadProofCoordinator(1);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = 0;
        Thread? worker = null;
        var pending = Task.Run(() => Record.Exception(() => proofs.ProbePrefix(0, 128, _ =>
        {
            worker = Thread.CurrentThread;
            started.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
            Interlocked.Increment(ref finished);
            return true;
        }, TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);
        Exception? disposeFailure = null;
        var completedAtDispose = 0;
        var disposer = new Thread(() =>
        {
            disposeFailure = Record.Exception(proofs.Dispose);
            completedAtDispose = Volatile.Read(ref finished);
        }) { IsBackground = true };
        var disposerStarted = false;

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            disposer.Start();
            disposerStarted = true;
            WaitForBlocked(disposer);
            if (interruptJoin)
                disposer.Interrupt();
            Assert.False(pending.IsCompleted);
            Assert.True(disposer.IsAlive);
        }
        finally
        {
            release.Set();
            if (disposerStarted)
                disposer.Join();
            await pending;
        }

        Assert.IsType<ObjectDisposedException>(await pending);
        Assert.Null(disposeFailure);
        Assert.Equal(1, completedAtDispose);
        Assert.Equal(1, Volatile.Read(ref finished));
        Assert.NotNull(worker);
        Assert.False(worker.IsAlive);
    }

    [Fact(Timeout = 10000)]
    public async Task InterruptedBatchWait_PreservesTheInterruptionAfterDrainingAndJoining()
    {
        using var proofs = new WindowsReadProofCoordinator(1);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var finished = 0;
        var completedAtReturn = 0;
        Thread? worker = null;
        var caller = new Thread(() =>
        {
            failure = Record.Exception(() => proofs.ProbePrefix(0, 128, _ =>
            {
                worker = Thread.CurrentThread;
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
                Interlocked.Increment(ref finished);
                return true;
            }, TestContext.Current.CancellationToken));
            completedAtReturn = Volatile.Read(ref finished);
        }) { IsBackground = true };
        caller.Start();
        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            WaitForBlocked(caller);
            caller.Interrupt();
        }
        finally
        {
            release.Set();
            caller.Join();
        }

        Assert.IsType<ThreadInterruptedException>(failure);
        Assert.Equal(completedAtReturn, Volatile.Read(ref finished));
        Assert.NotNull(worker);
        Assert.False(worker.IsAlive);
    }

    [Fact(Timeout = 10000)]
    public async Task EarlierReservedFalse_LowersThePrefixAfterALaterFalseAndDrainsEveryLowerIndex()
    {
        using var proofs = new WindowsReadProofCoordinator(3);
        using var lowerStarted = new CountdownEvent(2);
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseEarlierFalse = new ManualResetEventSlim();
        using var releaseLaterFalse = new ManualResetEventSlim();
        var allReserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var earlierFalse = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterFalse = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visits = new int[128];
        var finished = 0;
        var cancellationToken = TestContext.Current.CancellationToken;
        var pending = Task.Run(() =>
        {
            caller.SetResult(Thread.CurrentThread);
            return proofs.ProbePrefix(0, visits.Length, index =>
            {
                Interlocked.Increment(ref visits[index]);
                try
                {
                    if (index == 0)
                    {
                        lowerStarted.Signal();
                        releaseFirst.Wait(cancellationToken);
                        return true;
                    }
                    if (index == 1)
                    {
                        lowerStarted.Signal();
                        releaseEarlierFalse.Wait(cancellationToken);
                        earlierFalse.SetResult(Thread.CurrentThread);
                        return false;
                    }
                    lowerStarted.Wait(cancellationToken);
                    allReserved.SetResult();
                    releaseLaterFalse.Wait(cancellationToken);
                    laterFalse.SetResult(Thread.CurrentThread);
                    return false;
                }
                finally { Interlocked.Increment(ref finished); }
            }, cancellationToken);
        }, cancellationToken);

        try
        {
            await allReserved.Task.WaitAsync(cancellationToken);
            // All callbacks are outside the coordinator gate, so this is the caller's drain wait.
            WaitForBlocked(await caller.Task.WaitAsync(cancellationToken));
            releaseLaterFalse.Set();
            var laterWorker = await laterFalse.Task.WaitAsync(cancellationToken);
            // This callback has no wait after its signal, so an idle worker has recorded the later false.
            WaitForBlocked(laterWorker);
            Assert.False(pending.IsCompleted);
            releaseEarlierFalse.Set();
            var earlierWorker = await earlierFalse.Task.WaitAsync(cancellationToken);
            WaitForBlocked(earlierWorker);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            releaseLaterFalse.Set();
            releaseEarlierFalse.Set();
            releaseFirst.Set();
            await pending;
        }

        Assert.Equal(1, await pending);
        Assert.Equal(3, Volatile.Read(ref finished));
        Assert.Equal(new[] { 1, 1, 1 }, visits[..3]);
        Assert.All(visits[3..], static count => Assert.Equal(0, count));
    }

    private static void WaitForBlocked(Thread thread) => Assert.True(SpinWait.SpinUntil(
        () => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
}
