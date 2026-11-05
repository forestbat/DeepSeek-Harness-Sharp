using System.Runtime.ExceptionServices;

namespace Dsh.Sandbox;

/// <summary>Runs synchronous read-only proof batches on reusable dedicated threads.</summary>
/// <remarks>
/// A single caller submits batches; callbacks must not call back into this coordinator.
/// At most min(64, entryCount) workers exist, growing lazily to each batch's length.
/// False stops dispatch and drains every reserved callback before returning the first false index, or the end when all pass.
/// Failure, cancellation and disposal also join every started worker and never return a reusable prefix.
/// Batch callbacks are released before completion, and disposal drains synchronous native calls.
/// Callback errors remain primary when cancellation also arrives.
/// This bounds proof workers and in-flight callbacks, not the process's paths, security descriptors or total memory.
/// </remarks>
internal sealed class WindowsReadProofCoordinator : IDisposable
{
    private const int MaximumWorkers = 64;
    private readonly object _gate = new();
    private readonly Thread?[] _workers;
    private int _started;
    private int _next;
    private int _end;
    private int _active;
    private int _prefixEnd;
    private bool _running;
    private bool _stopped;
    private bool _shutdown;
    private Func<int, bool>? _probe;
    private ExceptionDispatchInfo? _failure;
    private CancellationToken _cancellationToken;

    public WindowsReadProofCoordinator(int entryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryCount);
        _workers = new Thread?[Math.Min(MaximumWorkers, entryCount)];
    }

    public int ProbePrefix(int start, int end, Func<int, bool> probe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        if (start == end)
            cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown, this);
            if (_running)
                throw new InvalidOperationException("Only one read proof batch may run at a time.");
            if (start == end)
                return end;
            _running = true;
            _stopped = false;
            _next = start;
            _end = end;
            _prefixEnd = end;
            _probe = probe;
            _cancellationToken = cancellationToken;
        }
        // Registration lives only for this batch, so neither the token nor idle workers retain its callback.
        using var registration = cancellationToken.UnsafeRegister(static state =>
            ((WindowsReadProofCoordinator)state!).Cancel(), this);
        StartWorkers(end - start);
        return DrainBatch();
    }

    private void Cancel()
    {
        lock (_gate)
            if (_running)
                Fail(new OperationCanceledException(_cancellationToken));
    }

    private void StartWorkers(int batchLength)
    {
        lock (_gate)
        {
            try
            {
                var required = Math.Min(_workers.Length, batchLength);
                while (_started < required && _next < _end && !_shutdown && !_stopped)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var worker = new Thread(Run) { IsBackground = true };
                    _workers[_started] = worker;
                    worker.Start();
                    _started++;
                }
            }
            catch (Exception error)
            {
                Fail(error);
            }
            Monitor.PulseAll(_gate);
        }
    }

    private int DrainBatch()
    {
        ExceptionDispatchInfo? failure;
        int prefixEnd;
        lock (_gate)
        {
            while (_active != 0 || !_stopped && _next < _end)
            {
                try { Monitor.Wait(_gate); }
                catch (ThreadInterruptedException error) { Fail(error); }
            }
            if (_cancellationToken.IsCancellationRequested)
                Fail(new OperationCanceledException(_cancellationToken));
            prefixEnd = _prefixEnd;
            failure = _failure;
            _probe = null;
            _running = false;
            _cancellationToken = default;
        }
        if (failure is not null)
        {
            JoinWorkers();
            failure.Throw();
        }
        return prefixEnd;
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                Func<int, bool>? probe;
                int index;
                lock (_gate)
                {
                    while (!_shutdown && (!_running || _stopped || _next == _end))
                        Monitor.Wait(_gate);
                    if (_shutdown)
                        return;
                    _cancellationToken.ThrowIfCancellationRequested();
                    index = _next++;
                    probe = _probe!;
                    _active++;
                }
                try
                {
                    if (!probe(index))
                        lock (_gate)
                        {
                            _prefixEnd = Math.Min(_prefixEnd, index);
                            _stopped = true;
                        }
                }
                catch (Exception error) { Fail(error); }
                finally
                {
                    probe = null;
                    lock (_gate)
                    {
                        _active--;
                        Monitor.PulseAll(_gate);
                    }
                }
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        lock (_gate)
        {
            if (_failure is null || _failure.SourceException is OperationCanceledException && error is not OperationCanceledException)
                _failure = ExceptionDispatchInfo.Capture(error);
            _stopped = true;
            _shutdown = true;
            Monitor.PulseAll(_gate);
        }
    }

    private void JoinWorkers()
    {
        for (var index = 0; index < _started; index++)
        {
            while (true)
            {
                try
                {
                    _workers[index]!.Join();
                    break;
                }
                catch (ThreadInterruptedException error) { Fail(error); }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_running && _failure is null)
                Fail(new ObjectDisposedException(nameof(WindowsReadProofCoordinator)));
            _shutdown = true;
            _stopped = true;
            _probe = null;
            Monitor.PulseAll(_gate);
        }
        var failure = _failure;
        JoinWorkers();
        // A previously reported batch failure remains primary after its workers have been joined.
        if (failure is null)
            _failure?.Throw();
    }
}
