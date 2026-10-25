using System.Net;
using System.Net.Sockets;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dsh.Runtime;
using Dsh.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Dsh.Tests;

public sealed class OutboundRouteTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetirementTimeout = TimeSpan.FromSeconds(5);
    private const string OldCredential = "route-old:secret-old";
    private const string NewCredential = "route-new:secret-new";
    private const string Http3OriginFile = "http3-origin.txt";
    private const string Http3UnavailableReason = "Isolated HTTP/3 requires Linux and the optional native QUIC runtime.";

    public static bool IsolatedHttp3Supported
        => OperatingSystem.IsLinux() && QuicConnection.IsSupported && QuicListener.IsSupported;

    private static readonly string[] ProxyEnvironmentNames = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"];

    [Fact(Explicit = true)]
    public async Task UnchangedRoute_ReusesTheEstablishedProxyConnection()
    {
        await using var server = new TestForwardProxy(requiredCredential: OldCredential, keepAlive: true);
        var proxy = new OutboundProxy();
        proxy.Configure($"http://{OldCredential}@127.0.0.1:{server.Port}", []);
        using var client = new HttpClient(proxy.CreateHandler());
        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync("http://example.com/first", TestContext.Current.CancellationToken));
        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync("http://example.com/second", TestContext.Current.CancellationToken));
        Assert.Equal(1, server.Connections);
        Assert.Equal([OldCredential, OldCredential], server.Credentials.ToArray());
        Assert.Equal(["GET http://example.com/first HTTP/1.1", "GET http://example.com/second HTTP/1.1"], server.Requests.ToArray());
        client.Dispose();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(SignalTimeout);
        Assert.Equal(1, await server.WaitForClosedConnectionAsync(deadline.Token));
        Assert.Equal(0, server.ActiveConnections);
    }

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RouteChange_AbortsPausedRequest_ClosesOldPool_AndUsesFreshCredentials(bool sameEndpoint)
    {
        await using var original = new TestForwardProxy(requiredCredential: OldCredential, keepAlive: true, pauseResponses: true);
        await using var replacement = sameEndpoint ? null : new TestForwardProxy(requiredCredential: NewCredential, keepAlive: true, pauseResponses: true);
        var next = replacement ?? original;
        var proxy = new OutboundProxy();
        proxy.Configure($"http://{OldCredential}@127.0.0.1:{original.Port}", []);
        using var client = new HttpClient(proxy.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(SignalTimeout);
        original.ReleaseResponse();
        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync("http://example.com/warm", TestContext.Current.CancellationToken).WaitAsync(deadline.Token));
        Assert.Equal("GET http://example.com/warm HTTP/1.1", await original.WaitForRequestAsync(deadline.Token));
        var paused = client.GetStringAsync("http://example.com/paused", TestContext.Current.CancellationToken);
        Assert.Equal("GET http://example.com/paused HTTP/1.1", await original.WaitForRequestAsync(deadline.Token));
        Assert.Equal(1, original.Connections);
        if (sameEndpoint)
            original.RequireCredential(NewCredential);

        proxy.Configure($"http://{NewCredential}@127.0.0.1:{next.Port}", []);

        var failure = await Record.ExceptionAsync(async () => await paused.WaitAsync(RetirementTimeout, TestContext.Current.CancellationToken));
        Assert.NotNull(failure);
        Assert.True(failure is HttpRequestException or OperationCanceledException or IOException,
            $"Expected old-generation cancellation or transport failure, got {failure.GetType().Name}.");
        original.ReleaseResponse();
        Assert.Equal(1, await original.WaitForClosedConnectionAsync(deadline.Token));
        Assert.Equal(0, original.ActiveConnections);
        var fresh = client.GetStringAsync("http://example.com/fresh", TestContext.Current.CancellationToken);
        Assert.Equal("GET http://example.com/fresh HTTP/1.1", await next.WaitForRequestAsync(deadline.Token));
        next.ReleaseResponse();
        Assert.Equal(TestForwardProxy.ProxiedBody, await fresh.WaitAsync(deadline.Token));
        Assert.Equal(sameEndpoint ? [OldCredential, OldCredential, NewCredential] : [NewCredential], next.Credentials.ToArray());
        Assert.Equal(sameEndpoint
            ? ["GET http://example.com/warm HTTP/1.1", "GET http://example.com/paused HTTP/1.1", "GET http://example.com/fresh HTTP/1.1"]
            : ["GET http://example.com/fresh HTTP/1.1"], next.Requests.ToArray());
        Assert.Equal(sameEndpoint ? 2 : 1, next.Connections);
        client.Dispose();
        Assert.Equal(sameEndpoint ? 2 : 1, await next.WaitForClosedConnectionAsync(deadline.Token));
        Assert.Equal(0, next.ActiveConnections);
    }

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedOrInvalidConfiguration_PreservesThePendingRequestAndPool(bool invalid)
    {
        await using var server = new TestForwardProxy(requiredCredential: OldCredential, keepAlive: true, pauseResponses: true);
        var proxy = new OutboundProxy();
        var url = $"http://{OldCredential}@127.0.0.1:{server.Port}";
        proxy.Configure(url, []);
        using var client = new HttpClient(proxy.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(SignalTimeout);
        var pending = client.GetStringAsync("http://example.com/preserved", TestContext.Current.CancellationToken);
        Assert.Equal("GET http://example.com/preserved HTTP/1.1", await server.WaitForRequestAsync(deadline.Token));
        if (invalid)
            Assert.Throws<ArgumentException>(() => proxy.Configure("unsupported://invalid.example", []));
        else
            proxy.Configure(url, []);
        server.ReleaseResponse();
        Assert.Equal(TestForwardProxy.ProxiedBody, await pending.WaitAsync(deadline.Token));
        server.ReleaseResponse();
        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync("http://example.com/reused", TestContext.Current.CancellationToken).WaitAsync(deadline.Token));
        Assert.Equal(1, server.Connections);
        Assert.Equal([OldCredential, OldCredential], server.Credentials.ToArray());
    }

    [Fact(Explicit = true)]
    public async Task IndependentDefaultRoutes_DoNotShareRetirement()
    {
        await using var origin = new TestForwardProxy(keepAlive: true, pauseResponses: true);
        var first = new OutboundProxy();
        var second = new OutboundProxy();
        using var client = new HttpClient(second.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(SignalTimeout);
        var pending = client.GetStringAsync($"http://127.0.0.1:{origin.Port}/independent", TestContext.Current.CancellationToken);
        Assert.Equal("GET /independent HTTP/1.1", await origin.WaitForRequestAsync(deadline.Token));
        first.Configure($"http://{NewCredential}@127.0.0.1:{origin.Port}", []);
        origin.ReleaseResponse();
        Assert.Equal(TestForwardProxy.ProxiedBody, await pending.WaitAsync(deadline.Token));
        Assert.Equal(1, origin.Connections);
        Assert.Empty(origin.Credentials);
    }

    [Fact(Explicit = true)]
    public async Task WebFetch_RetirementDuringDns_AbortsWithoutAdoptingTheNewRoute()
    {
        await using var original = new TestForwardProxy();
        await using var replacement = new TestForwardProxy();
        var proxy = new OutboundProxy();
        proxy.Configure($"http://127.0.0.1:{original.Port}", []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new HttpFetchProvider(resolveAddresses: async (_, signal) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(signal);
            return [new PublicAddress("1.1.1.1", 4)];
        }, proxy: proxy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(SignalTimeout);
        var pending = provider.Fetch(new WebFetchRequest("http://example.com/before-dns"), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            proxy.Configure($"http://127.0.0.1:{replacement.Port}", []);
            var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(RetirementTimeout, TestContext.Current.CancellationToken));
            var error = Assert.IsType<WebError>(failure);
            Assert.Equal(WebErrorCodes.Aborted, error.Code);
            Assert.Equal(0, original.Connections);
            Assert.Equal(0, replacement.Connections);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await pending.WaitAsync(deadline.Token);
            }
            catch (WebError)
            {
            }
        }
        var fresh = await provider.Fetch(new WebFetchRequest("http://example.com/after-dns"), TestContext.Current.CancellationToken);
        Assert.Equal(200, fresh.StatusCode);
        Assert.Equal(TestForwardProxy.ProxiedBody, fresh.Body.Content);
        Assert.Equal(["GET http://1.1.1.1/after-dns HTTP/1.1"], replacement.Requests.ToArray());
        Assert.Equal(0, original.Connections);
    }

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementAfterHeaders_StopsBodyReads_AlsoForPinnedDirectConnections(bool pinnedDirect)
    {
        await using var original = new TestForwardProxy(keepAlive: true, pauseBody: true);
        await using var replacement = new TestForwardProxy();
        var proxy = new OutboundProxy();
        proxy.Configure(pinnedDirect ? null : $"http://127.0.0.1:{original.Port}", []);
        var target = new Uri(pinnedDirect ? $"http://127.0.0.1:{original.Port}/body" : "http://example.com/body");
        using var client = new HttpClient(pinnedDirect
            ? proxy.CaptureRoute(target).CreateHandler(false, (context, token) => ConnectAsync(context.DnsEndPoint, token))
            : proxy.CreateHandler());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(SignalTimeout);
        using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken).WaitAsync(deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var pending = body.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask();

        proxy.Configure($"http://127.0.0.1:{replacement.Port}", []);

        var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(RetirementTimeout, TestContext.Current.CancellationToken));
        Assert.NotNull(failure);
        Assert.True(failure is IOException or OperationCanceledException or ObjectDisposedException,
            $"Expected an interrupted old-generation body, got {failure.GetType().Name}.");
        original.ReleaseResponse();
        Assert.Equal(1, await original.WaitForClosedConnectionAsync(deadline.Token));
        Assert.Equal(0, original.ActiveConnections);
        Assert.Equal(0, replacement.Connections);
        Assert.Equal(pinnedDirect ? ["GET /body HTTP/1.1"] : ["GET http://example.com/body HTTP/1.1"], original.Requests.ToArray());
    }

    [Fact(Explicit = true)]
    public async Task RouteChange_ClosesIdlePoolsOwnedByAllExistingClients()
    {
        await using var original = new TestForwardProxy(keepAlive: true, pauseResponses: true);
        await using var replacement = new TestForwardProxy(keepAlive: true);
        var proxy = new OutboundProxy();
        proxy.Configure($"http://127.0.0.1:{original.Port}", []);
        using var first = new HttpClient(proxy.CreateHandler());
        using var second = new HttpClient(proxy.CreateHandler());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(SignalTimeout);
        var firstWarm = first.GetStringAsync("http://example.com/first", TestContext.Current.CancellationToken);
        var secondWarm = second.GetStringAsync("http://example.com/second", TestContext.Current.CancellationToken);
        var received = new[] { await original.WaitForRequestAsync(deadline.Token), await original.WaitForRequestAsync(deadline.Token) };
        Assert.Equal(["GET http://example.com/first HTTP/1.1", "GET http://example.com/second HTTP/1.1"], received.Order().ToArray());
        original.ReleaseResponse();
        original.ReleaseResponse();
        Assert.Equal([TestForwardProxy.ProxiedBody, TestForwardProxy.ProxiedBody], await Task.WhenAll(firstWarm, secondWarm).WaitAsync(deadline.Token));
        Assert.Equal(2, original.Connections);

        proxy.Configure($"http://127.0.0.1:{replacement.Port}", []);

        var closed = new[] { await original.WaitForClosedConnectionAsync(deadline.Token), await original.WaitForClosedConnectionAsync(deadline.Token) };
        Assert.Equal([1, 2], closed.Order().ToArray());
        Assert.Equal(0, original.ActiveConnections);
        Assert.Equal(TestForwardProxy.ProxiedBody, await first.GetStringAsync("http://example.com/new-first", TestContext.Current.CancellationToken));
        Assert.Equal(TestForwardProxy.ProxiedBody, await second.GetStringAsync("http://example.com/new-second", TestContext.Current.CancellationToken));
        Assert.Equal(["GET http://example.com/new-first HTTP/1.1", "GET http://example.com/new-second HTTP/1.1"], replacement.Requests.ToArray());
    }

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BypassOrClear_RetiresTheOldProxy_AndTheNewPinnedRequestIsDirect(bool clear)
    {
        await using var original = new TestForwardProxy(keepAlive: true, pauseResponses: true);
        await using var origin = new TestForwardProxy();
        var proxy = new OutboundProxy();
        var proxyUrl = $"http://127.0.0.1:{original.Port}";
        proxy.Configure(proxyUrl, []);
        using var oldClient = new HttpClient(proxy.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(SignalTimeout);
        var pending = oldClient.GetStringAsync("http://example.com/before-bypass", TestContext.Current.CancellationToken);
        Assert.Equal("GET http://example.com/before-bypass HTTP/1.1", await original.WaitForRequestAsync(deadline.Token));

        proxy.Configure(clear ? null : proxyUrl, clear ? [] : ["example.com"]);

        var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(RetirementTimeout, TestContext.Current.CancellationToken));
        Assert.NotNull(failure);
        Assert.True(failure is HttpRequestException or OperationCanceledException or IOException,
            $"Expected an interrupted old proxy request, got {failure.GetType().Name}.");
        original.ReleaseResponse();
        Assert.Equal(1, await original.WaitForClosedConnectionAsync(deadline.Token));
        var target = new Uri("http://example.com/direct");
        using var direct = new HttpClient(proxy.CaptureRoute(target).CreateHandler(false,
            (_, token) => ConnectAsync(new IPEndPoint(IPAddress.Loopback, origin.Port), token)));
        Assert.Equal(TestForwardProxy.ProxiedBody, await direct.GetStringAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal(["GET /direct HTTP/1.1"], origin.Requests.ToArray());
        Assert.Contains("Host: example.com", origin.Headers);
        Assert.Equal(["GET http://example.com/before-bypass HTTP/1.1"], original.Requests.ToArray());
        Assert.Equal(0, original.ActiveConnections);
    }

    [Theory(Explicit = true)]
    [InlineData("http")]
    [InlineData("socks5")]
    public async Task SystemProxyCredentials_AuthenticateInAnIsolatedProcess(string scheme)
    {
        await using var proxy = new TestForwardProxy(socks: scheme == "socks5", requiredCredential: OldCredential);
        var child = await OutboundProxyProcess.RunAsync(
            $"{typeof(OutboundRouteTests).FullName}.{nameof(SystemProxyChild_ReadsTheControlledOrigin)}",
            $"{scheme}://{OldCredential}@127.0.0.1:{proxy.Port}", TestContext.Current.CancellationToken);
        Assert.True(child.ExitCode == 0, $"Authenticated system {scheme} routing failed in its isolated process.");
        Assert.Equal([OldCredential, OldCredential], proxy.Credentials.ToArray());
        Assert.Equal(scheme == "socks5"
            ? ["SOCKS 1.1.1.1:80", "GET /system-route HTTP/1.1", "SOCKS 1.1.1.1:80", "GET /system-route HTTP/1.1"]
            : ["GET http://1.1.1.1/system-route HTTP/1.1", "GET http://1.1.1.1/system-route HTTP/1.1"], proxy.Requests.ToArray());
    }

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemProxyChild_ReadsTheControlledOrigin(bool rawSnapshot)
    {
        if (ProxyEnvironmentNames.All(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
        {
            Assert.Skip("this entry point is run by the system-proxy parent with a controlled proxy");
            return;
        }
        var proxy = new OutboundProxy();
        var target = new Uri("http://1.1.1.1/system-route");
        using var client = new HttpClient(rawSnapshot ? proxy.CaptureRoute(target).CreateHandler(false) : proxy.CreateHandler());
        using var response = await client.GetAsync(target, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TestForwardProxy.ProxiedBody, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Explicit = true)]
    public async Task Http3Retirement_AfterHeaders_AbortsTheResponseAndOriginRequest()
    {
        if (!IsolatedHttp3Supported)
        {
            Assert.Skip(Http3UnavailableReason);
            return;
        }
        var directory = TempTree.CreateDirectory("or3");
        try
        {
            await using var origin = await Http3Origin.StartAsync(TestContext.Current.CancellationToken);
            var certificateFile = Path.Combine(directory, "root-ca.pem");
            await File.WriteAllTextAsync(certificateFile, origin.CertificatePem, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, Http3OriginFile), origin.Url, TestContext.Current.CancellationToken);
            var child = await OutboundProxyProcess.RunAsync(
                $"{typeof(OutboundRouteTests).FullName}.{nameof(Http3Child_RetiresThePausedBody)}",
                null, TestContext.Current.CancellationToken, certificateFile);
            Assert.True(child.ExitCode == 0, $"Isolated HTTP/3 retirement failed.\n{child.StandardOutput}\n{child.StandardError}");
            await origin.Aborted.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(1, origin.Requests);
        }
        finally
        {
            TempTree.Delete(directory);
        }
    }

    [Fact(Explicit = true)]
    public async Task Http3Child_RetiresThePausedBody()
    {
        var certificateFile = Environment.GetEnvironmentVariable("SSL_CERT_FILE");
        if (!IsolatedHttp3Supported || certificateFile is null)
        {
            Assert.Skip(Http3UnavailableReason);
            return;
        }
        var target = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(certificateFile)!, Http3OriginFile),
            TestContext.Current.CancellationToken);
        var proxy = new OutboundProxy();
        using var client = new HttpClient(proxy.CreateHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, target)
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpVersion.Version30, response.Version);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var first = new byte[1];
        Assert.Equal(1, await body.ReadAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal((byte)'a', first[0]);
        var pending = body.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask();
        await using var replacement = new TestForwardProxy();

        proxy.Configure($"http://127.0.0.1:{replacement.Port}", []);

        var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(RetirementTimeout, TestContext.Current.CancellationToken));
        Assert.NotNull(failure);
        Assert.True(failure is IOException or OperationCanceledException or ObjectDisposedException,
            $"Expected an interrupted HTTP/3 body, got {failure.GetType().Name}.");
        Assert.Equal(0, replacement.Connections);
    }

    /** Owns a real QUIC origin and an ephemeral test trust root, never a user certificate store. */
    private sealed class Http3Origin : IAsyncDisposable
    {
        private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
        private const int SerialNumberLength = 16;
        private static readonly TimeSpan CertificateClockSkew = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan CertificateLifetime = TimeSpan.FromHours(1);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly X509Certificate2 _certificate;
        private readonly WebApplication _application;
        private int _requests;

        private Http3Origin()
        {
            (CertificatePem, _certificate) = CreateCertificates();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseQuic();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint =>
            {
                endpoint.Protocols = HttpProtocols.Http3;
                endpoint.UseHttps(_certificate);
            }));
            _application = builder.Build();
            _application.Run(Serve);
        }

        internal string CertificatePem { get; }
        internal string Url => _application.Urls.Single();
        internal Task Aborted => _aborted.Task;
        internal int Requests => Volatile.Read(ref _requests);

        internal static async Task<Http3Origin> StartAsync(CancellationToken signal)
        {
            var origin = new Http3Origin();
            try
            {
                await origin._application.StartAsync(signal);
                return origin;
            }
            catch
            {
                await origin.DisposeAsync();
                throw;
            }
        }

        private async Task Serve(HttpContext context)
        {
            Interlocked.Increment(ref _requests);
            try
            {
                context.Response.ContentLength = 2;
                await context.Response.Body.WriteAsync(new byte[] { (byte)'a' }, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await _release.Task.WaitAsync(context.RequestAborted);
                await context.Response.Body.WriteAsync(new byte[] { (byte)'b' }, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                _aborted.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult();
            using var cleanup = new CancellationTokenSource(SignalTimeout);
            try
            {
                await _application.StopAsync(cleanup.Token);
            }
            finally
            {
                await _application.DisposeAsync();
                _certificate.Dispose();
            }
        }

        private static (string RootPem, X509Certificate2 Server) CreateCertificates()
        {
            var now = DateTimeOffset.UtcNow;
            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest("CN=Outbound route test root", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var root = rootRequest.CreateSelfSigned(now - CertificateClockSkew, now + CertificateLifetime);
            using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256);
            serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(ServerAuthenticationOid) }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            serverRequest.CertificateExtensions.Add(names.Build());
            using var certificate = serverRequest.Create(root, now - CertificateClockSkew, now + CertificateLifetime,
                RandomNumberGenerator.GetBytes(SerialNumberLength));
            return (root.ExportCertificatePem(), certificate.CopyWithPrivateKey(serverKey));
        }
    }

    private static async ValueTask<Stream> ConnectAsync(EndPoint endpoint, CancellationToken token)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint, token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

