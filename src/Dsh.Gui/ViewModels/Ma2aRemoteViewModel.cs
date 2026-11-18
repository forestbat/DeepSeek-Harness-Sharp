using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Gui.Services;
using Dsh.Gui.Terminal;
using Dsh.Ma2a;
using Dsh.Ma2a.Services;
using Dsh.Pty;

namespace Dsh.Gui.ViewModels;

public sealed partial class Ma2aRemoteViewModel(Func<Ma2aClient> client) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _operation;
    private Ma2aTerminalConnection? _terminalConnection;
    private Ma2aProxyForward? _forward;
    private Ma2aServiceSession? _activeSession;
    private Action? _terminalExitHandler;
    private long _generation;
    private JsonNode? _pendingBody;
    private Ma2aRemoteOperation? _pendingOperation;
    private IReadOnlyDictionary<string, JsonNode?>? _pendingValues;
    private Ma2aClient? _pendingClient;
    private string? _pendingTarget;
    private UncertainRequest? _uncertain;
    private sealed record UncertainRequest(Ma2aClient Client, string Target, Ma2aRemoteOperation Operation,
        IReadOnlyDictionary<string, JsonNode?> Values, JsonNode? Body, string RequestId, string OutputPath);
    private const int MaximumResultEntries = 128;

    public IReadOnlyList<Ma2aRemoteOperation> Operations { get; } = Ma2aRemoteOperation.Create();
    public ObservableCollection<Ma2aResultNode> Results { get; } = [];
    public IReadOnlyList<Ma2aFieldViewModel> Fields => SelectedOperation?.Fields ?? [];

    [ObservableProperty] private Ma2aRemoteOperation? _selectedOperation;
    [ObservableProperty] private string _targetEndpoint = "";
    [ObservableProperty] private string _status = "选择远端 Endpoint 和操作。文件路径使用 ROOT:/relative/path。";
    [ObservableProperty] private string _requestId = "";
    [ObservableProperty] private string _rawOutputPath = "";
    [ObservableProperty] private bool _confirmed;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty] private TerminalSession? _terminal;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _transferring;
    [ObservableProperty] private string _inputHex = "";
    [ObservableProperty] private string _controlHex = "";
    [ObservableProperty] private string _pendingRequestId = "";

    partial void OnSelectedOperationChanged(Ma2aRemoteOperation? value)
    {
        Confirmed = false;
        OnPropertyChanged(nameof(Fields));
    }

    [RelayCommand]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Busy || SelectedOperation is not { } operation) return;
        if (HasPending && operation.IsMutation) { Status = "上次结果尚未确认；可执行只读检查，或重试原始请求。"; return; }
        if (operation.IsMutation && !Confirmed) { Status = "请确认目标、操作和参数。"; return; }
        try
        {
            var values = operation.Fields.ToDictionary(field => field.Name, field => field.BuildValue());
            var body = operation.Build?.Invoke(values);
            _pendingClient = client();
            _pendingOperation = operation;
            _pendingValues = values;
            _pendingBody = body;
            _pendingTarget = TargetEndpoint;
            Ma2aServiceSession.DecodeIdentifier(TargetEndpoint, 32);
            RequestId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            await RunPreparedAsync(cancellationToken);
        }
        catch (Exception error) { Status = error.Message; }
    }

    [RelayCommand]
    private async Task RetryAsync(CancellationToken cancellationToken)
    {
        if (Busy || _uncertain is not { } request) return;
        _pendingClient = request.Client;
        _pendingTarget = request.Target;
        _pendingOperation = request.Operation;
        _pendingValues = request.Values;
        _pendingBody = request.Body;
        RequestId = request.RequestId;
        RawOutputPath = request.OutputPath;
        await RunPreparedAsync(cancellationToken);
    }

    private async Task RunPreparedAsync(CancellationToken cancellationToken)
    {
        _operation?.Dispose();
        _operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _generation++;
        Busy = true;
        Results.Clear();
        try
        {
            await CloseAttachmentsAsync();
            if (await TransferAsync(_operation.Token)) return;
            await OpenOperationAsync(_operation.Token);
            if (_pendingOperation!.IsMutation && !_pendingOperation.IsTerminal) ClearUncertain();
        }
        catch (Ma2aServiceException error)
        {
            if (!error.Rejected && _pendingOperation!.IsMutation) PreserveUncertain();
            Status = $"MA2A status {error.Status}: {error.Message}";
        }
        catch (Exception error)
        {
            if (_pendingOperation!.IsMutation) PreserveUncertain();
            Status = HasPending ? $"结果尚未确认；保留 RequestId。{error.Message}" : error.Message;
        }
        finally { if (Terminal is null && _forward is null) Busy = false; Transferring = false; }
    }

    private async Task<bool> TransferAsync(CancellationToken cancellationToken)
    {
        var operation = _pendingOperation!;
        if (operation.Family != "transfer" && operation.Name != "artifact") return false;
        Transferring = true;
        var values = _pendingValues!;
        var localPath = Ma2aRemoteOperation.S(values, "local_path");
        var replace = Ma2aRemoteOperation.B(values, "replace");
        var progress = new Progress<Ma2aTransferProgress>(value =>
        {
            Progress = value.Total == 0 ? 0 : (double)value.Completed / value.Total * 100;
            Status = $"传输 {value.Completed} / {value.Total} 字节";
        });
        Ma2aTransferResult result;
        if (operation.Name == "upload")
            result = await Ma2aFileTransfer.UploadAsync(_pendingClient!, _pendingTarget!, Ma2aRemoteOperation.S(values, "path"), localPath, RequestId, replace, progress, cancellationToken);
        else if (operation.Name == "artifact")
            result = await Ma2aFileTransfer.DownloadArtifactAsync(_pendingClient!, _pendingTarget!, Ma2aRemoteOperation.S(values, "agent"), Ma2aRemoteOperation.S(values, "task_id"), Ma2aRemoteOperation.S(values, "name"), localPath, RequestId, replace, progress: progress, cancellationToken: cancellationToken);
        else
            result = await Ma2aFileTransfer.DownloadAsync(_pendingClient!, _pendingTarget!, Ma2aRemoteOperation.S(values, "path"), localPath, RequestId, replace, progress: progress, cancellationToken: cancellationToken);
        AddResult("verified transfer", new JsonObject
        {
            [nameof(result.Bytes)] = result.Bytes,
            [nameof(result.Size)] = result.Size,
            [nameof(result.Blake3)] = result.Blake3,
        });
        Status = $"传输完成，已验证 BLAKE3：{result.Blake3}";
        if (operation.IsMutation) ClearUncertain();
        return true;
    }

    private async Task OpenOperationAsync(CancellationToken cancellationToken)
    {
        var operation = _pendingOperation!;
        if (operation.Family == "proxy")
        {
            _forward = new Ma2aProxyForward(_pendingClient!, _pendingTarget!, _pendingBody!, (ushort)Ma2aRemoteOperation.U(_pendingValues!, "local_port"), Report, cancellationToken);
            Status = $"转发监听 127.0.0.1:{_forward.LocalPort}；每个连接单独接受 MA2A 授权。";
            return;
        }
        var session = await Ma2aServiceSession.OpenAsync(_pendingClient!, _pendingTarget!, operation.Family, RequestId, _pendingBody!, cancellationToken);
        if (operation.IsTerminal)
        {
            var generation = _generation;
            _terminalConnection = new Ma2aTerminalConnection(session, operation.Family == "shell", operation.Name != "process attach" || Ma2aRemoteOperation.B(_pendingValues!, "stdin"), node => Dispatcher.UIThread.Post(() =>
            {
                if (generation == _generation) AddResult("terminal", node);
            }), error => OnTerminalFailure(error, generation), cancellationToken);
            Terminal = new TerminalSession(_terminalConnection, new TerminalSize(80, 24));
            _terminalExitHandler = () => OnTerminalExited(generation);
            Terminal.Exited += _terminalExitHandler;
            Terminal.Start();
            Status = "终端已附着。取消会断开此流；持久 Process 继续运行。";
            return;
        }
        _activeSession = session;
        try { await using (session) await ReadOperationAsync(session, cancellationToken); }
        finally { _activeSession = null; }
    }

    private async Task ReadOperationAsync(Ma2aServiceSession session, CancellationToken cancellationToken)
    {
        await using var output = string.IsNullOrWhiteSpace(RawOutputPath) ? null
            : new FileStream(RawOutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65_536, FileOptions.Asynchronous);
        using var exchanging = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task sending = Task.CompletedTask;
        Task<Ma2aServiceFrame>? reading = null;
        try
        {
            while (true)
            {
                reading = session.ReadAsync(exchanging.Token);
                if (!sending.IsCompletedSuccessfully)
                {
                    await Task.WhenAny(reading, sending);
                    if (!reading.IsCompleted && !session.IsTerminal) await sending;
                }
                var frame = await reading;
                reading = null;
                if (frame.Kind == Ma2aFrameKind.Data) await ReceiveDataAsync(frame, output, exchanging.Token);
                else if (frame.Kind == Ma2aFrameKind.Control && session.Service == "service") AddResult("Control bytes", Ma2aCbor.Bytes(frame.Payload.Span));
                else if (frame.Kind is Ma2aFrameKind.Accept or Ma2aFrameKind.Control or Ma2aFrameKind.Outcome) AddResult(frame.Kind.ToString(), frame.DecodeCbor());
                if (frame.Kind == Ma2aFrameKind.Accept && _pendingOperation!.Name is "process stdin" or "write") sending = SendInputAsync(session, exchanging.Token);
                if (frame.Kind == Ma2aFrameKind.Accept && _pendingOperation!.Name == "call") sending = StartHostedInputAsync(session, exchanging.Token);
                if (frame.Kind != Ma2aFrameKind.Outcome) continue;
                Status = "远端操作已完成。";
                return;
            }
        }
        finally
        {
            await exchanging.CancelAsync();
            try { await sending; }
            catch (Exception) when (exchanging.IsCancellationRequested) { }
            try { if (reading is not null) await reading; }
            catch (Exception) when (exchanging.IsCancellationRequested) { }
        }
    }

    private async Task SendInputAsync(Ma2aServiceSession session, CancellationToken cancellationToken)
    {
        var input = _pendingOperation!.Name == "write"
            ? Convert.FromHexString(Ma2aRemoteOperation.S(_pendingValues!, "content_hex"))
            : Encoding.UTF8.GetBytes(Ma2aRemoteOperation.S(_pendingValues!, "input"));
        await session.SendDataAsync(0, input, cancellationToken);
        await session.EndAsync(cancellationToken: cancellationToken);
    }

    private async Task ReceiveDataAsync(Ma2aServiceFrame frame, Stream? output, CancellationToken cancellationToken)
    {
        if (output is not null) await output.WriteAsync(frame.Data, cancellationToken);
        if (_pendingOperation?.Name == "watch")
        {
            AddResult("Agent event", Ma2aCbor.Decode(frame.Data));
            return;
        }
        AddResult($"channel {frame.Channel}: {frame.Data.Length} bytes", new JsonObject
        {
            ["hex_preview"] = Convert.ToHexStringLower(frame.Data.Span[..Math.Min(frame.Data.Length, 128)]),
            ["saved"] = output is not null,
        });
    }

    private async Task StartHostedInputAsync(Ma2aServiceSession session, CancellationToken cancellationToken)
    {
        if (Ma2aRemoteOperation.O(_pendingValues!, "input_file") is { } path)
            await SendHostedFileAsync(session, path, cancellationToken);
        else
        {
            var bytes = Convert.FromHexString(Ma2aRemoteOperation.S(_pendingValues!, "input_hex"));
            if (bytes.Length > 0) await session.SendDataAsync(0, bytes, cancellationToken);
        }
        if (!Ma2aRemoteOperation.B(_pendingValues!, "keep_input_open")) await session.EndAsync(cancellationToken: cancellationToken);
    }

    private static async Task SendHostedFileAsync(Ma2aServiceSession session, string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            Ma2aServiceSession.MaximumDataLength, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[Ma2aServiceSession.MaximumDataLength];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            await session.SendDataAsync(0, buffer.AsMemory(0, count), cancellationToken);
    }

    [RelayCommand]
    private async Task SendBytesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_activeSession is not null) await _activeSession.SendDataAsync(0, Convert.FromHexString(InputHex), cancellationToken);
        }
        catch (Exception error) { Status = error.Message; }
    }

    [RelayCommand]
    private async Task SendControlAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_pendingOperation?.Family == "service" && _activeSession is not null)
                await _activeSession.ControlBytesAsync(Convert.FromHexString(ControlHex), cancellationToken);
        }
        catch (Exception error) { Status = error.Message; }
    }

    private void AddResult(string label, JsonNode? value)
    {
        if (Results.Count >= MaximumResultEntries) Results.RemoveAt(0);
        using var document = JsonDocument.Parse(value?.ToJsonString() ?? "null");
        Results.Add(new Ma2aResultNode(label, Ma2aResultNode.ChildrenOf(document.RootElement)));
    }

    private void Report(string message) => Dispatcher.UIThread.Post(() => Status = message);

    private void OnTerminalExited(long generation) => Dispatcher.UIThread.Post(() =>
    {
        if (generation != _generation) return;
        Status = "终端操作已结束；终态详情见结果。";
        if (_uncertain?.RequestId == RequestId) ClearUncertain();
        Busy = false;
    });

    private void OnTerminalFailure(Exception error, long generation) => Dispatcher.UIThread.Post(() =>
    {
        if (generation != _generation) return;
        if (error is not Ma2aServiceException { Rejected: true }) PreserveUncertain();
        Busy = false;
        Status = $"终端流已结束：{error.Message}";
    });

    [RelayCommand]
    private async Task EndInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_terminalConnection is not null) await _terminalConnection.EndInputAsync(cancellationToken);
            else if (_activeSession is not null) await _activeSession.EndAsync(cancellationToken: cancellationToken);
        }
        catch (Exception error) { Status = error.Message; }
    }

    [RelayCommand]
    public void Cancel() => _operation?.Cancel();

    [RelayCommand]
    private async Task DetachAsync()
    {
        if (Busy && _pendingOperation?.IsMutation == true) PreserveUncertain();
        Cancel();
        await CloseAttachmentsAsync();
        Busy = false;
        Status = "已解除流附着；持久进程和 Agent 任务保持独立生命周期。";
    }

    [RelayCommand]
    private void AcknowledgePending()
    {
        if (!Confirmed) { Status = "请核实目标状态并勾选确认。"; return; }
        ClearUncertain();
        Status = "已确认上次结果；下次执行将创建新请求。";
    }

    private async Task CloseAttachmentsAsync()
    {
        if (Terminal is { } terminal) { terminal.Exited -= _terminalExitHandler; _terminalExitHandler = null; await terminal.DisposeAsync(); Terminal = null; }
        if (_terminalConnection is { } connection) { _terminalConnection = null; await connection.DisposeAsync(); }
        if (_forward is { } forward) { _forward = null; await forward.DisposeAsync(); }
    }

    private void PreserveUncertain()
    {
        if (_pendingOperation?.IsMutation != true) return;
        _uncertain = new(_pendingClient!, _pendingTarget!, _pendingOperation, _pendingValues!, _pendingBody, RequestId, RawOutputPath);
        PendingRequestId = RequestId;
        HasPending = true;
    }

    private void ClearUncertain()
    {
        _uncertain = null;
        PendingRequestId = "";
        HasPending = false;
    }

    public void Dispose()
    {
        Cancel();
        _ = CloseAttachmentsAsync();
    }
}
