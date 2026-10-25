using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dsh.Runtime;

namespace Dsh.Boot;

/** Raw transport reachability and its diagnostic phase, independent of business success. */
public readonly record struct OutboundProxyProbeResult(bool Reachable, string Message, string Stage, int? HttpStatus = null);

/**
 * Connectivity probing shared by local commands and the target Host backend.
 * TCP reachability and the subsequent HTTP response are reported under the caller's outbound owner.
 */
public static class OutboundProxyProbe
{
    public const string ConfigurationStage = "configuration";
    public const string ProxyConnectStage = "proxy_connect";
    public const string ProxyAuthStage = "proxy_auth";
    public const string HttpTargetStage = "http_target";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public static async Task<OutboundProxyProbeResult> RunAsync(OutboundProxySettings? settings, CancellationToken cancellationToken, string ownerLabel = "本机出口", Action<string>? stageChanged = null)
    {
        stageChanged?.Invoke(ConfigurationStage);
        cancellationToken.ThrowIfCancellationRequested();
        using var proxy = new OutboundProxy();
        try
        {
            proxy.Configure(settings?.Url, settings?.Bypass);
        }
        catch (ArgumentException error)
        {
            return new OutboundProxyProbeResult(false, OutboundProxy.RedactUrlCredentials($"{ownerLabel}代理不通: 配置无效({error.Message})", settings?.Url, settings?.ProbeUrl), ConfigurationStage);
        }
        var probeText = string.IsNullOrWhiteSpace(settings?.ProbeUrl) ? OutboundProxySettings.DefaultProbeUrl : settings.ProbeUrl.Trim();
        if (!Uri.TryCreate(probeText, UriKind.Absolute, out var probe) || probe.Scheme is not ("http" or "https"))
            return new OutboundProxyProbeResult(false, $"{ownerLabel}探测地址无效(需要 http/https URL)", ConfigurationStage);
        var endpoint = proxy.ProxyFor(probe);
        var layer = proxy.Address is null ? $"{ownerLabel}(未配置{ownerLabel}代理, 跟随系统代理)"
            : endpoint is null ? $"{ownerLabel}(探测地址在直连名单内)"
            : $"{ownerLabel}代理";
        var via = endpoint is null ? "直连" : proxy.Address is null ? OutboundProxy.RedactUrlCredentials(endpoint.GetLeftPart(UriPartial.Authority), settings?.Url, probeText) : proxy.Describe();
        if (endpoint is not null)
        {
            stageChanged?.Invoke(ProxyConnectStage);
            if (await ConnectFailureAsync(endpoint, cancellationToken) is { } connectError)
                return new OutboundProxyProbeResult(false, OutboundProxy.RedactUrlCredentials($"{layer}不通: 无法连接代理 {via}({connectError})", settings?.Url, probeText), ProxyConnectStage);
        }
        stageChanged?.Invoke(HttpTargetStage);
        return await RequestAsync(proxy, probe, layer, via, settings?.Url, cancellationToken, stageChanged);
    }

    private static async Task<string?> ConnectFailureAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"{ConnectTimeout.TotalSeconds:0} 秒内未连上";
        }
        catch (SocketException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return error.Message;
        }
    }

    private static async Task<OutboundProxyProbeResult> RequestAsync(OutboundProxy proxy, Uri probe, string layer, string via, string? proxyUrl, CancellationToken cancellationToken, Action<string>? stageChanged)
    {
        using var client = new HttpClient(proxy.CreateHandler(allowAutoRedirect: false)) { Timeout = RequestTimeout };
        var watch = Stopwatch.StartNew();
        HttpStatusCode status;
        try
        {
            using var response = await client.GetAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            status = response.StatusCode;
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.ProxyAuthenticationRequired && !cancellationToken.IsCancellationRequested)
        {
            status = HttpStatusCode.ProxyAuthenticationRequired;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reason = error is OperationCanceledException ? $"{RequestTimeout.TotalSeconds:0} 秒内无响应" : Flatten(error);
            var httpStatus = (error as HttpRequestException)?.StatusCode;
            return new OutboundProxyProbeResult(false, OutboundProxy.RedactUrlCredentials($"{layer}不通: 经 {via} 访问 {probe.AbsoluteUri} 失败({reason})", proxyUrl, probe.AbsoluteUri), HttpTargetStage, httpStatus is { } code ? (int)code : null);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (status == HttpStatusCode.ProxyAuthenticationRequired)
        {
            stageChanged?.Invoke(ProxyAuthStage);
            cancellationToken.ThrowIfCancellationRequested();
            return new OutboundProxyProbeResult(false, OutboundProxy.RedactUrlCredentials($"{layer}不通: 代理 {via} 要求认证或用户名密码错误(HTTP 407)", proxyUrl, probe.AbsoluteUri), ProxyAuthStage, (int)status);
        }
        return new OutboundProxyProbeResult(true, OutboundProxy.RedactUrlCredentials($"{layer}出口可达: 经 {via} 访问 {probe.AbsoluteUri} 返回 HTTP {(int)status}, 耗时 {watch.ElapsedMilliseconds} ms", proxyUrl, probe.AbsoluteUri), HttpTargetStage, (int)status);
    }

    private static string Flatten(Exception error)
        => error.InnerException is { } inner && inner.Message != error.Message ? $"{error.Message}: {inner.Message}" : error.Message;
}
