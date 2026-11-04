using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Workspace;
using Dsh.Runtime;

namespace Dsh.Gui.ViewModels;

/** One diagnostic owns its cancellation and result generation, not the shared Fleet connection. */
public sealed partial class NetworkProbeViewModel(ProxyProbeLayer layer) : ObservableObject
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _cancellationCallbacks;
    private Task _completion = Task.CompletedTask;
    private long _generation;
    private bool _cancelRequested;
    private string _machine = "";
    private string? _home;

    public ProxyProbeLayer Layer => layer;

    public string Label => layer switch
    {
        ProxyProbeLayer.Ssh => "SSH 连接代理",
        ProxyProbeLayer.LocalOutbound => "本机出口代理",
        ProxyProbeLayer.RemoteOutbound => "远端服务器出口代理",
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutcomeText), nameof(IsPassed), nameof(HasResult))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutcomeText), nameof(StageText), nameof(HttpStatusText), nameof(IdentityText), nameof(Detail), nameof(IsPassed), nameof(HasResult))]
    private ProxyProbeResult? _result;

    public string OutcomeText => IsRunning ? "检查中…" : Result?.Outcome switch
    {
        ProxyProbeOutcome.Passed => "通过",
        ProxyProbeOutcome.Failed => "失败",
        ProxyProbeOutcome.Cancelled => "已取消",
        ProxyProbeOutcome.Unsupported => "不支持",
        _ => "",
    };

    public string StageText => Result?.Stage switch
    {
        ProxyProbeStage.Configuration => "配置",
        ProxyProbeStage.Ssh => "SSH 连接",
        ProxyProbeStage.ProxyConnect => "代理连接",
        ProxyProbeStage.ProxyAuth => "代理认证",
        ProxyProbeStage.HttpTarget => "HTTP 目标",
        ProxyProbeStage.Tunnel => "借用隧道",
        _ => "",
    };

    public string HttpStatusText => Result?.HttpStatus is { } status ? $"HTTP {status}" : "";

    public string IdentityText => Result is { } result
        ? $"{result.Machine} · {result.Home ?? "尚未取得 Host home"}"
        : _machine.Length == 0 ? "" : $"{_machine} · {_home ?? "尚未取得 Host home"}";

    public string Detail => Result?.Detail ?? "";

    public bool IsPassed => !IsRunning && Result?.Outcome == ProxyProbeOutcome.Passed;

    public bool HasResult => IsRunning || Result is not null;

    public Task RunAsync(string machine, string? home, Func<Guid, CancellationToken, Task<ProxyProbeResult>> probe,
        params string?[] secrets)
    {
        lock (_gate)
        {
            if (IsRunning)
                throw new InvalidOperationException("A diagnostic is already running in this layer.");
            _machine = machine;
            _home = home;
            Result = null;
            OnPropertyChanged(nameof(IdentityText));
            _cancellation = new CancellationTokenSource();
            _cancellationCallbacks = null;
            _cancelRequested = false;
            IsRunning = true;
            _completion = RunCoreAsync(++_generation, Guid.NewGuid(), _cancellation, probe, secrets);
            return _completion;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            ++_generation;
            Result = null;
            _machine = "";
            _home = null;
            OnPropertyChanged(nameof(IdentityText));
            if (_cancellation is { } cancellation)
                _cancellationCallbacks ??= cancellation.CancelAsync();
        }
    }

    public Task WaitForCompletionAsync()
    {
        lock (_gate)
            return _completion;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task CancelAsync()
    {
        Task completion;
        lock (_gate)
        {
            if (IsRunning)
            {
                _cancelRequested = true;
                if (Result is { Outcome: ProxyProbeOutcome.Passed } result)
                    Result = result with { Outcome = ProxyProbeOutcome.Cancelled, Detail = "检查已取消。" };
                if (_cancellation is { } cancellation)
                    _cancellationCallbacks ??= cancellation.CancelAsync();
            }
            completion = _completion;
        }
        await completion;
    }

    private async Task RunCoreAsync(long generation, Guid operationId, CancellationTokenSource cancellation,
        Func<Guid, CancellationToken, Task<ProxyProbeResult>> probe, string?[] secrets)
    {
        try
        {
            var result = await probe(operationId, cancellation.Token);
            var detail = OutboundProxy.Redact(result.Detail, secrets);
            Publish(generation, detail == result.Detail ? result : result with { Detail = detail });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Publish(generation, new ProxyProbeResult(layer, ProxyProbeOutcome.Cancelled, ProxyProbeStage.Configuration,
                "检查已取消。", null, _machine, _home, operationId));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var outcome = error is NotSupportedException ? ProxyProbeOutcome.Unsupported : ProxyProbeOutcome.Failed;
            Publish(generation, new ProxyProbeResult(layer, outcome, ProxyProbeStage.Configuration,
                OutboundProxy.Redact(error.Message, secrets), null, _machine, _home, operationId));
        }
        finally
        {
            Task? callbacks;
            lock (_gate)
            {
                callbacks = _cancellationCallbacks;
                _cancellation = null;
            }
            try
            {
                if (callbacks is not null)
                    await callbacks;
            }
            finally
            {
                cancellation.Dispose();
                lock (_gate)
                    IsRunning = false;
            }
        }
    }

    private void Publish(long generation, ProxyProbeResult result)
    {
        lock (_gate)
        {
            if (_generation != generation)
                return;
            Result = _cancelRequested && result.Outcome == ProxyProbeOutcome.Passed
                ? result with { Outcome = ProxyProbeOutcome.Cancelled, Detail = "检查已取消。" }
                : result;
        }
    }
}
