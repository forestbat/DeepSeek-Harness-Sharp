using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Integration;

internal static class ServicesScenarios
{
    public static async Task RunAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(pair.Root, "files"));
        await pair.ExposeAsync("files_root", new JsonObject
        {
            ["root"] = "data", ["path"] = Path.Combine(pair.Root, "files"), ["access"] = "read_write"
        }, cancellationToken);
        await pair.ExposeAsync("exec", cancellationToken: cancellationToken);
        await pair.ExposeAsync("process", cancellationToken: cancellationToken);
        await AuthorizationAsync(pair, cancellationToken);
        await NativeRuntimePair.PassedAsync("Service grants restored; beginning native Files scenarios.");
        await FilesAsync(pair, cancellationToken);
        await TransfersAsync(pair, cancellationToken);
        await ExecAndProcessAsync(pair, cancellationToken);
        await AgentServiceScenarios.RunAsync(pair, cancellationToken);
    }

    private static string RequestId() => Guid.NewGuid().ToString("N");

    private static async Task AuthorizationAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        await Console.Out.WriteLineAsync("STAGE beginning signed service grant withdrawal.");
        await pair.GrantAsync(false, cancellationToken);
        await pair.SyncAsync(cancellationToken);
        await NativeRuntimePair.PassedAsync("Grant withdrawn and synchronized; requesting native Files refusal.");
        try
        {
            await RunAsync(pair, "files", Ma2aServiceRequests.Stat("data:/"), null, cancellationToken);
            throw new InvalidOperationException("A reachable Endpoint served Files without the caller grant.");
        }
        catch (Ma2aServiceException error) when (error.Status == 3 && error.Rejected) { }
        await pair.GrantAsync(true, cancellationToken);
        await pair.SyncAsync(cancellationToken);
        await NativeRuntimePair.PassedAsync("reachable native Files stream rejects a revoked grant before admission");
    }

    private static async Task FilesAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var content = Encoding.UTF8.GetBytes("MA2A native component test: 文件\n");
        await RunAsync(pair, "files", Ma2aServiceRequests.Write("data:/small.txt", (uint)content.Length), content, cancellationToken);
        var (result, read) = await RunAsync(pair, "files", Ma2aServiceRequests.Read("data:/small.txt"), null, cancellationToken);
        NativeRuntimePair.Check(read.SequenceEqual(content), "Files Read returns exactly the content written through native IPC.");
        NativeRuntimePair.Check(result is JsonObject, "Files operation has a successful outcome envelope.");
        await RunAsync(pair, "files", Ma2aServiceRequests.Rename("data:/small.txt", "data:/renamed.txt"), null, cancellationToken);
        var (_, renamed) = await RunAsync(pair, "files", Ma2aServiceRequests.Read("data:/renamed.txt"), null, cancellationToken);
        NativeRuntimePair.Check(renamed.SequenceEqual(content), "Files Rename preserves content.");
        await RunAsync(pair, "files", Ma2aServiceRequests.Remove("data:/renamed.txt"), null, cancellationToken);
        await NativeRuntimePair.PassedAsync("native Files write/read/rename/remove");
    }

    private static async Task TransfersAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var source = Path.Combine(pair.Root, "transfer-source.bin");
        var destination = Path.Combine(pair.Root, "transfer-result.bin");
        var bytes = new byte[524_288];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(source, bytes, cancellationToken);
        var uploaded = await Ma2aFileTransfer.UploadAsync(pair.Caller, pair.TargetId, "data:/transfer.bin", source,
            RequestId(), cancellationToken: cancellationToken);
        NativeRuntimePair.Check(uploaded.Size == (ulong)bytes.Length, "Upload commits the exact file length.");
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progress = new CancelAfterProgress(interrupted);
        try
        {
            await Ma2aFileTransfer.DownloadAsync(pair.Caller, pair.TargetId, "data:/transfer.bin", destination,
                RequestId(), progress: progress, cancellationToken: interrupted.Token);
            throw new InvalidOperationException("Expected the download to be interrupted before publication.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        NativeRuntimePair.Check(!File.Exists(destination) && File.Exists($"{destination}{Ma2aFileTransfer.PartialFileSuffix}.meta"),
            "Cancellation retains source identity and leaves destination unpublished.");
        var downloaded = await Ma2aFileTransfer.DownloadAsync(pair.Caller, pair.TargetId, "data:/transfer.bin", destination,
            RequestId(), cancellationToken: cancellationToken);
        var actual = await File.ReadAllBytesAsync(destination, cancellationToken);
        NativeRuntimePair.Check(actual.SequenceEqual(bytes) && uploaded.Blake3 == downloaded.Blake3,
            "Resumed download verifies BLAKE3 over the complete prefix and suffix.");
        NativeRuntimePair.Check(!File.Exists($"{destination}{Ma2aFileTransfer.PartialFileSuffix}.meta"), "Verified publication removes the resume metadata.");
        await NativeRuntimePair.PassedAsync("native transfer upload, cancellation, durable resume and BLAKE3 integrity");
    }

    private sealed class CancelAfterProgress(CancellationTokenSource cancellation) : IProgress<Ma2aTransferProgress>
    {
        public void Report(Ma2aTransferProgress value)
        {
            if (value.Completed > 0 && value.Completed < value.Total) cancellation.Cancel();
        }
    }

    private static async Task ExecAndProcessAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        await Console.Out.WriteLineAsync("STAGE beginning real native Exec.");
        var program = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        string[] args = OperatingSystem.IsWindows() ? ["/c", "echo ma2a-native"] : ["-c", "printf ma2a-native"];
        var (_, output) = await RunAsync(pair, "exec", Ma2aServiceRequests.Exec(program, args), null, cancellationToken);
        NativeRuntimePair.Check(Encoding.UTF8.GetString(output).Trim() == "ma2a-native", "Native Exec delivers actual process output.");
        await NativeRuntimePair.PassedAsync("Native Exec delivered the expected stdout; beginning durable Process.");
        var persistentProgram = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/cat";
        string[] persistentArgs = OperatingSystem.IsWindows() ? ["/c", "findstr ."] : [];
        var (started, _) = await RunAsync(pair, "process", Ma2aServiceRequests.ProcessStart(persistentProgram, persistentArgs, stdin: true), null, cancellationToken);
        await NativeRuntimePair.PassedAsync("Durable Process start returned; opening a separate inspection stream.");
        var processId = started?["1"]?[1]?[0]?["$bytes"]?.GetValue<string>()
            ?? throw new InvalidDataException("Process start did not return its identifier.");
        try
        {
            await WaitForRunningProcessAsync(pair, processId, cancellationToken);
            await NativeRuntimePair.PassedAsync("Durable Process inspected; attaching stdin and output.");
            var input = Encoding.UTF8.GetBytes("ma2a-persistent\n");
            var (_, attached) = await RunAsync(pair, "process", Ma2aServiceRequests.ProcessAttach(processId, stdin: true), input, cancellationToken);
            NativeRuntimePair.Check(Encoding.UTF8.GetString(attached).Trim() == "ma2a-persistent",
                "An attachment receives retained/live output and closes durable stdin explicitly.");
            var (_, logs) = await RunAsync(pair, "process", Ma2aServiceRequests.ProcessLogs(processId), null, cancellationToken);
            NativeRuntimePair.Check(Encoding.UTF8.GetString(logs).Trim() == "ma2a-persistent", "Durable Process output survives attachment closure.");
            await NativeRuntimePair.PassedAsync("native Exec and durable Process start/inspect/attach/stdin/logs");
        }
        finally
        {
            var terminate = NativeRuntimePair.Mutation("process_terminate");
            terminate["process_id"] = processId;
            terminate["grace_ms"] = 0;
            await pair.Target.CallAsync(terminate, CancellationToken.None);
        }
    }

    private static async Task WaitForRunningProcessAsync(NativeRuntimePair pair, string processId, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            var (inspected, _) = await RunAsync(pair, "process", Ma2aServiceRequests.ProcessInspect(processId), null, cancellationToken);
            NativeRuntimePair.Check(inspected?["1"]?[0]?.GetValue<ulong>() == 2, "A later native stream can inspect the durable Process.");
            var summary = inspected?["1"]?[1]?[0] ?? throw new InvalidDataException("Process inspection omitted its summary.");
            var state = summary[2]!.GetValue<ulong>();
            if (state == 2) return;
            NativeRuntimePair.Check(state < 2, $"Process ended before attachment (state: {state}, reason: {summary[3]?.ToJsonString() ?? "none"}).");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        throw new TimeoutException("Durable Process did not enter Running within its startup admission budget.");
    }

    internal static async Task<(JsonNode? Result, byte[] Output)> RunAsync(NativeRuntimePair pair, string service,
        JsonNode request, byte[]? input, CancellationToken cancellationToken)
    {
        await using var session = await Ma2aServiceSession.OpenAsync(pair.Caller, pair.TargetId, service,
            RequestId(), request, cancellationToken);
        using var output = new MemoryStream();
        while (true)
        {
            var frame = await session.ReadAsync(cancellationToken);
            switch (frame.Kind)
            {
                case Ma2aFrameKind.Accept when input is not null:
                    await session.SendDataAsync(0, input, cancellationToken);
                    await session.EndAsync(cancellationToken: cancellationToken);
                    break;
                case Ma2aFrameKind.Data:
                    if (output.Length + frame.Data.Length > 1_048_576) throw new InvalidDataException("Test output exceeded its bound.");
                    await output.WriteAsync(frame.Data, cancellationToken);
                    break;
                case Ma2aFrameKind.Outcome: return (frame.DecodeCbor(), output.ToArray());
            }
        }
    }
}
