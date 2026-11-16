using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Integration;

internal static class CrossMachineScenarios
{
    private const int TransferBytes = 524_288;
    private const int MaximumOutputBytes = 1_048_576;
    private static readonly TimeSpan ProcessStartupBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcessObservationInterval = TimeSpan.FromMilliseconds(100);

    public static async Task RunAsync(string stateDirectory, string targetEndpointId, string authorizedRoot,
        CancellationToken cancellationToken, string platform = "linux")
    {
        ValidateLocalState(stateDirectory);
        if (platform is not ("linux" or "windows")) throw new ArgumentException("Remote platform must be linux or windows.", nameof(platform));
        _ = Ma2aServiceRequests.Path($"{authorizedRoot}:/");
        var client = new Ma2aClient(stateDirectory);
        var endpoint = NativeRuntimePair.Payload(await client.CallAsync(NativeRuntimePair.Command("endpoint_info"), cancellationToken));
        NativeRuntimePair.Check(endpoint.GetProperty("endpoint_id").GetString() != targetEndpointId, "The target must be a different Runtime Endpoint.");
        var run = Guid.NewGuid().ToString("N");
        var local = Path.Combine(stateDirectory, $"cross-{run}");
        Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, ".gitignore"), "*\n", cancellationToken);
        await EchoAsync(client, targetEndpointId, cancellationToken);
        await FilesAndTransferAsync(client, targetEndpointId, authorizedRoot, run, local, cancellationToken);
        await ExecAsync(client, targetEndpointId, platform, cancellationToken);
        await DurableProcessAsync(client, targetEndpointId, platform, cancellationToken);
        await NativeRuntimePair.PassedAsync("Cross-machine native Echo, Files, verified Transfer, Exec and durable Process scenarios.");
    }

    private static void ValidateLocalState(string stateDirectory)
    {
        NativeRuntimePair.Check(Path.IsPathFullyQualified(stateDirectory) && Directory.Exists(stateDirectory), "An existing absolute local Runtime state directory is required.");
        var directory = new DirectoryInfo(Path.GetFullPath(stateDirectory));
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".git")) && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;
        NativeRuntimePair.Check(directory is not null, "Cross-machine acceptance state must stay inside a Git repository.");
    }

    private static async Task EchoAsync(Ma2aClient client, string target, CancellationToken cancellationToken)
    {
        const string content = "cross-machine-native-echo";
        var command = NativeRuntimePair.Mutation("echo_call");
        command["target_endpoint_id"] = target;
        command["payload"] = content;
        var response = NativeRuntimePair.Payload(await client.CallAsync(command, cancellationToken));
        NativeRuntimePair.Check(response.GetProperty("target_endpoint_id").GetString() == target && response.GetProperty("payload").GetString() == content,
            "Remote Echo must authenticate the exact target and preserve the payload.");
        await NativeRuntimePair.PassedAsync("Cross-machine native Echo authenticated the configured target.");
    }

    private static async Task FilesAndTransferAsync(Ma2aClient client, string target, string root, string run,
        string local, CancellationToken cancellationToken)
    {
        var small = $"{root}:/dsh-cross-{run}.txt";
        var remoteTransfer = $"{root}:/dsh-cross-{run}.bin";
        var content = Encoding.UTF8.GetBytes("Native cross-machine file roundtrip: 文件\n");
        var smallCreated = false;
        var transferCreated = false;
        try
        {
            await CallAsync(client, target, "files", Ma2aServiceRequests.Write(small, (uint)content.Length), content, cancellationToken);
            smallCreated = true;
            var (_, read) = await CallAsync(client, target, "files", Ma2aServiceRequests.Read(small), null, cancellationToken);
            NativeRuntimePair.Check(read.SequenceEqual(content), "Cross-machine Files must read the exact bytes written.");
            await NativeRuntimePair.PassedAsync("Cross-machine native Files write/read preserved UTF-8 bytes.");
            var source = Path.Combine(local, "source.bin");
            var destination = Path.Combine(local, "received.bin");
            var bytes = new byte[TransferBytes];
            Random.Shared.NextBytes(bytes);
            await File.WriteAllBytesAsync(source, bytes, cancellationToken);
            var uploaded = await Ma2aFileTransfer.UploadAsync(client, target, remoteTransfer, source, RequestId(), cancellationToken: cancellationToken);
            transferCreated = true;
            var downloaded = await Ma2aFileTransfer.DownloadAsync(client, target, remoteTransfer, destination, RequestId(), cancellationToken: cancellationToken);
            var received = await File.ReadAllBytesAsync(destination, cancellationToken);
            NativeRuntimePair.Check(uploaded.Size == (ulong)bytes.Length && downloaded.Size == uploaded.Size
                && uploaded.Blake3 == downloaded.Blake3 && bytes.SequenceEqual(received),
                "Cross-machine transfers must match their complete bytes, size and BLAKE3 digest.");
            await NativeRuntimePair.PassedAsync("Cross-machine native Transfer verified complete content with BLAKE3.");
        }
        finally
        {
            var errors = new List<Exception>();
            if (smallCreated) await RemoveOwnedFileAsync(client, target, small, errors);
            if (transferCreated) await RemoveOwnedFileAsync(client, target, remoteTransfer, errors);
            if (errors.Count != 0) throw new AggregateException("Cross-machine acceptance file cleanup failed.", errors);
        }
    }

    private static async Task RemoveOwnedFileAsync(Ma2aClient client, string target, string path, List<Exception> errors)
    {
        try { await CallAsync(client, target, "files", Ma2aServiceRequests.Remove(path), null, CancellationToken.None); }
        catch (Exception error) { errors.Add(error); }
    }

    private static async Task ExecAsync(Ma2aClient client, string target, string platform, CancellationToken cancellationToken)
    {
        var program = platform == "windows" ? "cmd.exe" : "/bin/sh";
        string[] arguments = platform == "windows" ? ["/c", "echo cross-machine-native-exec"] : ["-c", "printf cross-machine-native-exec"];
        var (_, output) = await CallAsync(client, target, "exec", Ma2aServiceRequests.Exec(program, arguments), null, cancellationToken);
        NativeRuntimePair.Check(Encoding.UTF8.GetString(output).Trim() == "cross-machine-native-exec", "Cross-machine Exec must return actual remote process output.");
        await NativeRuntimePair.PassedAsync("Cross-machine native Exec returned the remote program's stdout.");
    }

    private static async Task DurableProcessAsync(Ma2aClient client, string target, string platform, CancellationToken cancellationToken)
    {
        var program = platform == "windows" ? "cmd.exe" : "/bin/cat";
        string[] arguments = platform == "windows" ? ["/c", "findstr ."] : [];
        var (started, _) = await CallAsync(client, target, "process", Ma2aServiceRequests.ProcessStart(program, arguments, stdin: true), null, cancellationToken);
        var id = started?["1"]?[1]?[0]?["$bytes"]?.GetValue<string>()
            ?? throw new InvalidDataException("The remote Process start omitted its identifier.");
        try
        {
            await WaitForProcessAsync(client, target, id, cancellationToken);
            var input = Encoding.UTF8.GetBytes("cross-machine-durable-process\n");
            var (_, attached) = await CallAsync(client, target, "process", Ma2aServiceRequests.ProcessAttach(id, stdin: true), input, cancellationToken);
            NativeRuntimePair.Check(Encoding.UTF8.GetString(attached).Trim() == "cross-machine-durable-process", "The remote durable Process must accept stdin after its creating stream closes.");
            var (_, logs) = await CallAsync(client, target, "process", Ma2aServiceRequests.ProcessLogs(id), null, cancellationToken);
            NativeRuntimePair.Check(Encoding.UTF8.GetString(logs).Trim() == "cross-machine-durable-process", "A later stream must read the remote Process retained output.");
            await NativeRuntimePair.PassedAsync("Cross-machine durable Process start/inspect/attach/stdin/logs survives separate native streams.");
        }
        finally
        {
            await CallAsync(client, target, "process", Ma2aServiceRequests.ProcessTerminate(id), null, CancellationToken.None);
            await CallAsync(client, target, "process", Ma2aServiceRequests.ProcessDelete(id), null, CancellationToken.None);
        }
    }

    private static async Task WaitForProcessAsync(Ma2aClient client, string target, string id, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < ProcessStartupBudget)
        {
            var (response, _) = await CallAsync(client, target, "process", Ma2aServiceRequests.ProcessInspect(id), null, cancellationToken);
            NativeRuntimePair.Check(response?["1"]?[0]?.GetValue<ulong>() == 2, "A separate cross-machine stream must inspect the created Process.");
            var state = response?["1"]?[1]?[0]?[2]?.GetValue<ulong>() ?? throw new InvalidDataException("Remote Process inspection omitted state.");
            if (state == 2) return;
            NativeRuntimePair.Check(state < 2, "The remote Process ended before attachment.");
            await Task.Delay(ProcessObservationInterval, cancellationToken);
        }
        throw new TimeoutException("The remote durable Process did not enter its Running state.");
    }

    private static string RequestId() => Guid.NewGuid().ToString("N");

    private static async Task<(JsonNode? Outcome, byte[] Output)> CallAsync(Ma2aClient client, string target, string family,
        JsonNode request, byte[]? input, CancellationToken cancellationToken)
    {
        await using var session = await Ma2aServiceSession.OpenAsync(client, target, family, RequestId(), request, cancellationToken);
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
                    if (output.Length + frame.Data.Length > MaximumOutputBytes) throw new InvalidDataException("Cross-machine acceptance output exceeded its bound.");
                    await output.WriteAsync(frame.Data, cancellationToken);
                    break;
                case Ma2aFrameKind.Outcome: return (frame.DecodeCbor(), output.ToArray());
            }
        }
    }
}
