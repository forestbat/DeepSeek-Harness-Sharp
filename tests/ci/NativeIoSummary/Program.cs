using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

if (args is ["--help"])
{
    await Console.Out.WriteLineAsync("Usage: NativeIoSummary TRACE ROOT_PID");
    return 0;
}
if (!OperatingSystem.IsWindows() || args.Length != 2 || !int.TryParse(args[1], out var rootPid))
{
    await Console.Out.WriteLineAsync("NATIVE_IO_UNAVAILABLE code=1");
    return 2;
}
try
{
    using var source = new ETWTraceEventSource(args[0]);
    var summary = new NativeIoSummary(rootPid);
    summary.Attach(source);
    source.Process();
    await summary.WriteAsync(source.EventsLost);
    return 0;
}
catch (Exception exception)
{
    await Console.Out.WriteLineAsync(FormattableString.Invariant($"NATIVE_IO_UNAVAILABLE code=2 hresult={exception.HResult}"));
    return 2;
}

sealed class NativeIoSummary(int rootPid)
{
    private const int MaximumPending = 65536;
    private const uint PagingIo = 2;
    private enum OperationKind { DirectoryEnumeration, Create, Cleanup, Close, QueryInformation, DiskRead, DiskWrite }
    private static readonly Guid FileIoProvider = new("90cbdc39-4a3e-11d1-84f4-0000f80464e3");
    private readonly Dictionary<int, double> _owned = [];
    private readonly Dictionary<ulong, PendingIo> _pending = [];
    private readonly IoTotals[] _operations = [new(), new(), new(), new(), new()];
    private readonly IoTotals _diskRead = new();
    private readonly IoTotals _diskWrite = new();
    private int _rootStarts, _rootStops, _descendants, _collisions, _overflow;
    private long _unhandledFileIo, _unknownEnds, _unknownProcessEvents, _pagingDisk;

    public void Attach(ETWTraceEventSource source)
    {
        var kernel = new KernelTraceEventParser(source);
        kernel.ProcessStart += StartProcess;
        kernel.ProcessStop += StopProcess;
        kernel.FileIODirEnum += data => Begin(data, data.IrpPtr, OperationKind.DirectoryEnumeration);
        kernel.FileIOCreate += data => Begin(data, data.IrpPtr, OperationKind.Create);
        kernel.FileIOCleanup += data => Begin(data, data.IrpPtr, OperationKind.Cleanup);
        kernel.FileIOClose += data => Begin(data, data.IrpPtr, OperationKind.Close);
        kernel.FileIOQueryInfo += data => Begin(data, data.IrpPtr, OperationKind.QueryInformation);
        kernel.FileIOOperationEnd += End;
        kernel.DiskIORead += data => Disk(data, _diskRead);
        kernel.DiskIOWrite += data => Disk(data, _diskWrite);
        source.UnhandledEvents += data =>
        {
            if (data.ProviderGuid == FileIoProvider)
                _unhandledFileIo++;
        };
    }

    private void StartProcess(ProcessTraceData data)
    {
        if (data.ProcessID == rootPid)
        {
            _rootStarts++;
            if (_rootStarts == 1)
                _owned[data.ProcessID] = data.TimeStampRelativeMSec;
            else
                _owned.Remove(data.ProcessID);
        }
        else if (_rootStarts == 1 && _owned.ContainsKey(data.ParentID))
        {
            _owned[data.ProcessID] = data.TimeStampRelativeMSec;
            _descendants++;
        }
        else
            _owned.Remove(data.ProcessID);
    }

    private void StopProcess(ProcessTraceData data)
    {
        if (_owned.Remove(data.ProcessID) && data.ProcessID == rootPid)
            _rootStops++;
    }

    private void Begin(TraceEvent data, ulong irp, OperationKind kind)
    {
        if (data.ProcessID < 0)
            _unknownProcessEvents++;
        if (_rootStarts != 1 || !_owned.TryGetValue(data.ProcessID, out var generation) || data.TimeStampRelativeMSec < generation)
            return;
        _operations[(int)kind].Started++;
        if (_pending.Count >= MaximumPending)
        {
            _overflow++;
            return;
        }
        if (!_pending.TryAdd(irp, new PendingIo(kind, data.TimeStampRelativeMSec)))
            _collisions++;
    }

    private void End(FileIOOpEndTraceData data)
    {
        if (_pending.Remove(data.IrpPtr, out var operation))
            _operations[(int)operation.Kind].Add(data.TimeStampRelativeMSec - operation.Start);
        else if (_owned.ContainsKey(data.ProcessID))
            _unknownEnds++;
    }

    private void Disk(DiskIOTraceData data, IoTotals totals)
    {
        if (data.ProcessID < 0)
            _unknownProcessEvents++;
        if (_rootStarts != 1 || !_owned.TryGetValue(data.ProcessID, out var generation) || data.TimeStampRelativeMSec < generation)
            return;
        totals.Started++;
        totals.Add(data.ElapsedTimeMSec);
        totals.Bytes += data.TransferSize;
        if (((uint)data.IrpFlags & PagingIo) != 0)
            _pagingDisk++;
    }

    public async Task WriteAsync(long lost)
    {
        var endpoints = _rootStarts == 1 && _rootStops == 1;
        await Console.Out.WriteLineAsync(FormattableString.Invariant($"NATIVE_IO_CAPTURE root_pid={rootPid} root_starts={_rootStarts} root_stops={_rootStops} descendants={_descendants} endpoint_complete={(endpoints ? 1 : 0)} events_lost={lost} unhandled_fileio={_unhandledFileIo} unknown_process_events={_unknownProcessEvents} unmatched_owned_ends={_unknownEnds} pending_owned={_pending.Count} collisions={_collisions} overflow={_overflow}"));
        for (var kind = 0; kind < _operations.Length; kind++)
            await _operations[kind].WriteAsync(kind);
        await _diskRead.WriteAsync((int)OperationKind.DiskRead);
        await _diskWrite.WriteAsync((int)OperationKind.DiskWrite);
        await Console.Out.WriteLineAsync(FormattableString.Invariant($"NATIVE_IO_LIMITS paging_disk={_pagingDisk} cross_layer_irp_join=0 security_query_attribution=0 accesscheck_attribution=0 minifilter_schema_verified=0"));
    }

    private readonly record struct PendingIo(OperationKind Kind, double Start);

    private sealed class IoTotals
    {
        public long Started, Matched, Bytes;
        private double _sum, _maximum;

        public void Add(double milliseconds)
        {
            if (!double.IsFinite(milliseconds) || milliseconds < 0)
                return;
            Matched++;
            _sum += milliseconds;
            _maximum = Math.Max(_maximum, milliseconds);
        }

        public Task WriteAsync(int kind) => Console.Out.WriteLineAsync(FormattableString.Invariant($"NATIVE_IO_OPERATION kind={kind} started={Started} matched={Matched} bytes={Bytes} sum_ms={_sum:F3} max_ms={_maximum:F3}"));
    }
}
