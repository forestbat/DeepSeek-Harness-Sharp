using System.ComponentModel;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Dsh.Boot;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Fleet;

public enum ProxyProbeLayer
{
    [JsonStringEnumMemberName("ssh")] Ssh,
    [JsonStringEnumMemberName("local_outbound")] LocalOutbound,
    [JsonStringEnumMemberName("remote_outbound")] RemoteOutbound,
}

public enum ProxyProbeOutcome
{
    Passed,
    Failed,
    Cancelled,
    Unsupported,
}

public enum ProxyProbeStage
{
    [JsonStringEnumMemberName("configuration")] Configuration,
    [JsonStringEnumMemberName("ssh")] Ssh,
    [JsonStringEnumMemberName("proxy_connect")] ProxyConnect,
    [JsonStringEnumMemberName("proxy_auth")] ProxyAuth,
    [JsonStringEnumMemberName("http_target")] HttpTarget,
    [JsonStringEnumMemberName("tunnel")] Tunnel,
}

public sealed record ProxyProbeResult(ProxyProbeLayer Layer, ProxyProbeOutcome Outcome, ProxyProbeStage Stage,
    string Detail, int? HttpStatus, string Machine, string? Home, Guid OperationId)
{
    private const int InterruptedExitCode = 130;

    public int ExitCode => Outcome switch
    {
        ProxyProbeOutcome.Passed => 0,
        ProxyProbeOutcome.Cancelled => InterruptedExitCode,
        _ => 1,
    };

    public string Label => Layer switch
    {
        ProxyProbeLayer.Ssh => "SSH 连接代理",
        ProxyProbeLayer.LocalOutbound => "本机出口代理",
        ProxyProbeLayer.RemoteOutbound => "远端服务器出口代理",
        _ => throw new ArgumentOutOfRangeException(nameof(Layer)),
    };
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProxyProbeResult))]
public partial class FleetProxyProbeJsonContext : JsonSerializerContext;

/** User-triggered diagnostics reuse the existing SSH transport and local or target-Host HTTP probe. */
public sealed class FleetProxyProbe(FleetOptions options)
{
    public static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(35);
    private const int LayerCount = 3;
    private readonly int[] _running = new int[LayerCount];
    private readonly ProxyProbeResult?[] _results = new ProxyProbeResult?[LayerCount];

    public ProxyProbeResult? ResultFor(ProxyProbeLayer layer) => Volatile.Read(ref _results[(int)layer]);

    public Task<ProxyProbeResult> ProbeSshAsync(MachineSpec machine, Guid operationId, CancellationToken cancellationToken)
        => RunAsync(new Operation(ProxyProbeLayer.Ssh, machine.Name, machine.HarnessHome, operationId, machine),
            (operation, token) => ProbeSshCoreAsync(operation, machine, token), cancellationToken);

    public Task<ProxyProbeResult> ProbeLocalAsync(string home, OutboundProxySettings? settings, Guid operationId, CancellationToken cancellationToken)
        => RunAsync(new Operation(ProxyProbeLayer.LocalOutbound, MachineSpec.LocalName, home, operationId),
            (operation, token) => ProbeLocalCoreAsync(operation, settings, token), cancellationToken);

    public Task<ProxyProbeResult> ProbeRemoteAsync(MachineConnection connection, RemoteOutboundProxyProbeRequest request,
        Guid operationId, CancellationToken cancellationToken)
        => RunAsync(new Operation(ProxyProbeLayer.RemoteOutbound, connection.Machine.Name,
                connection.Machine.HarnessHome, operationId, connection.Machine),
            (operation, token) => ProbeRemoteCoreAsync(operation, connection, request, token), cancellationToken);

    private async Task ProbeSshCoreAsync(Operation operation, MachineSpec machine, CancellationToken cancellationToken)
    {
        if (machine.IsLocal)
        {
            operation.SetResult(ProxyProbeOutcome.Unsupported, "Select an SSH machine to check its SSH connection.");
            return;
        }
        operation.Stage = ProxyProbeStage.Ssh;
        var check = await FleetDoctor.CheckSshAsync(machine, new SshTransport(machine, options.SshTransportAdapter), cancellationToken).ConfigureAwait(false);
        var passed = check.Outcome == DoctorOutcome.Ok;
        // Doctor details are local diagnostics and may contain raw SSH stderr or private paths.
        operation.SetResult(passed ? ProxyProbeOutcome.Passed : ProxyProbeOutcome.Failed,
            passed
                ? "SSH connected and the remote connectivity command completed successfully."
                : "SSH connection or remote connectivity command failed. Check SSH authentication, connectivity, and proxy configuration.");
    }

    private static async Task ProbeLocalCoreAsync(Operation operation, OutboundProxySettings? settings, CancellationToken cancellationToken)
    {
        var result = await OutboundProxyProbe.RunAsync(settings, cancellationToken, "本机出口", operation.SetStage).ConfigureAwait(false);
        operation.SetStage(result.Stage);
        operation.SetResult(result.Reachable ? ProxyProbeOutcome.Passed : ProxyProbeOutcome.Failed, result.Message, result.HttpStatus);
    }

    private static async Task ProbeRemoteCoreAsync(Operation operation, MachineConnection connection,
        RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken)
    {
        operation.Stage = ProxyProbeStage.Ssh;
        var result = await new FleetNetwork(connection, operation.BeginHttpProbe).ProbeOutboundAsync(
            request with { OperationId = operation.OperationId }, cancellationToken).ConfigureAwait(false);
        if (result.Stage is null || result.HostHome is null || result.Reachable && result.HttpStatus is null)
            throw new NotSupportedException("The target Host does not provide structured proxy diagnostics; upgrade the target Host.");
        operation.Home = result.HostHome;
        operation.SetStage(result.Stage);
        operation.SetResult(result.Reachable ? ProxyProbeOutcome.Passed : ProxyProbeOutcome.Failed, result.Message, result.HttpStatus);
    }

    private async Task<ProxyProbeResult> RunAsync(Operation operation, Func<Operation, CancellationToken, Task> probe,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            operation.SetResult(ProxyProbeOutcome.Cancelled, "Probe cancelled by the caller.");
            return operation.Result();
        }
        var index = (int)operation.Layer;
        if (Interlocked.CompareExchange(ref _running[index], 1, 0) != 0)
        {
            operation.SetResult(ProxyProbeOutcome.Failed, "A probe for this layer is already running.");
            return operation.Result();
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeout = operation.Layer switch
            {
                ProxyProbeLayer.Ssh => SshTimeout,
                ProxyProbeLayer.LocalOutbound => LocalTimeout,
                _ => RemoteTimeout,
            };
            deadline.CancelAfter(timeout);
            await RunOperationAsync(operation, probe, deadline.Token, cancellationToken, timeout).ConfigureAwait(false);
            var result = operation.Result();
            Volatile.Write(ref _results[index], result);
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _running[index], 0);
        }
    }

    private static async Task RunOperationAsync(Operation operation, Func<Operation, CancellationToken, Task> probe,
        CancellationToken deadline, CancellationToken caller, TimeSpan timeout)
    {
        try
        {
            deadline.ThrowIfCancellationRequested();
            await probe(operation, deadline).ConfigureAwait(false);
            deadline.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            operation.SetResult(ProxyProbeOutcome.Cancelled, "Probe cancelled by the caller.");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            operation.SetResult(ProxyProbeOutcome.Failed, $"Probe exceeded its {timeout.TotalSeconds:0}-second deadline.");
        }
        catch (OperationCanceledException)
        {
            operation.SetResult(ProxyProbeOutcome.Failed, "Probe interrupted before completion.");
        }
        catch (NotSupportedException error)
        {
            operation.SetExceptionResult(ProxyProbeOutcome.Unsupported, error);
            operation.Stage = ProxyProbeStage.Configuration;
        }
        catch (Exception error) when (error is IOException or SocketException or HttpRequestException or JsonRpcException
            or InvalidOperationException or ArgumentException or Win32Exception or UnauthorizedAccessException)
        {
            operation.SetExceptionResult(ProxyProbeOutcome.Failed, error);
        }
    }

    private sealed class Operation(ProxyProbeLayer layer, string machine, string? home, Guid operationId, MachineSpec? target = null)
    {
        public ProxyProbeLayer Layer { get; } = layer;
        public Guid OperationId { get; } = operationId;
        public ProxyProbeStage Stage { get; set; } = ProxyProbeStage.Configuration;
        public string? Home { get; set; } = home;
        private ProxyProbeOutcome Outcome { get; set; } = ProxyProbeOutcome.Failed;
        private string Detail { get; set; } = "";
        private int? HttpStatus { get; set; }

        public void BeginHttpProbe(string actualHome)
        {
            Home = actualHome;
            Stage = ProxyProbeStage.HttpTarget;
        }

        public void SetStage(string stage) => Stage = stage switch
        {
            OutboundProxyProbe.ConfigurationStage => ProxyProbeStage.Configuration,
            OutboundProxyProbe.ProxyConnectStage => ProxyProbeStage.ProxyConnect,
            OutboundProxyProbe.ProxyAuthStage => ProxyProbeStage.ProxyAuth,
            OutboundProxyProbe.HttpTargetStage => ProxyProbeStage.HttpTarget,
            _ => throw new NotSupportedException("The target Host returned an unsupported proxy diagnostic stage; upgrade the target Host."),
        };

        public void SetResult(ProxyProbeOutcome outcome, string detail, int? httpStatus = null)
        {
            Outcome = outcome;
            Detail = detail;
            HttpStatus = httpStatus;
        }

        public void SetExceptionResult(ProxyProbeOutcome outcome, Exception error)
        {
            var detail = Layer == ProxyProbeLayer.Ssh || Stage == ProxyProbeStage.Ssh
                ? error switch
                {
                    NotSupportedException => "The configured SSH transport is unsupported or its adapter is unavailable.",
                    UnauthorizedAccessException => "SSH could not access a required local file or process. Check its access permissions.",
                    FileNotFoundException or DirectoryNotFoundException => "A required SSH executable, identity file, or adapter file was not found.",
                    Win32Exception => "The SSH executable or transport process could not start. Check its installation and access permissions.",
                    ArgumentException => "SSH transport configuration is invalid. Check the machine's SSH settings.",
                    _ => "SSH transport could not complete the probe. Check SSH authentication, connectivity, and proxy configuration.",
                }
                : error.Message;
            SetResult(outcome, detail);
        }

        public ProxyProbeResult Result() => new(Layer, Outcome, Stage,
            OutboundProxy.Redact(Detail, target?.Password, target?.Proxy), HttpStatus, machine, Home, OperationId);
    }
}
