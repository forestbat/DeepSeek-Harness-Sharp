using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;
using Dsh.Transport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dsh.WebBridge;


/** The independent WebHost owns HTTP, cookie authentication and WebSocket upgrades. Machine lifecycle and resumable streams belong to the shared Fleet client. */
public sealed class WebBridgeServer : IAsyncDisposable
{
    public const string WebSocketPath = "/ws";
    public const string LoginPath = "/api/login";
    public const string SessionPath = "/api/session";
    public const string MachinesPath = "/api/machines";
    public const string MachineParameter = "machine";
    public static readonly string DefaultClientName = $"web@{Environment.MachineName}";
    private const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    private readonly WebApplication _application;
    private readonly LocalEndpointToken _token;
    private readonly string _portFile;
    private readonly FleetClient _fleet;
    private readonly bool _ownsFleet;
    private int _disposed;

    private WebBridgeServer(WebApplication application, LocalEndpointToken token, string portFile, FleetClient fleet, int port, bool ownsFleet)
        => (_application, _token, _portFile, _fleet, Port, _ownsFleet) = (application, token, portFile, fleet, port, ownsFleet);

    public int Port { get; }

    public static WebBridgeServer Start(WebBridgeOptions options, FleetClient? fleet = null) => StartAsync(options, fleet).GetAwaiter().GetResult();

    private static async Task<WebBridgeServer> StartAsync(WebBridgeOptions options, FleetClient? suppliedFleet)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(WebBridgeServer).Assembly.GetName().Name });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, options.Port);
            server.Limits.MaxRequestBodySize = 64 * 1024;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        // Credentials and cookies expire with this process; no persistent key ring or reusable cookie crosses a bridge restart.
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAuthentication(Scheme).AddCookie(Scheme, cookie =>
        {
            cookie.Cookie.Name = WebAccessGate.CookieName;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            cookie.Cookie.Path = "/";
            cookie.SlidingExpiration = false;
            cookie.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            cookie.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
        var app = builder.Build();
        var portFile = WebBridgeEndpoint.PortFile(options.HostRoot);
        LocalEndpointToken? token = null;
        FleetClient? fleet = null;
        try
        {
            fleet = suppliedFleet ?? new FleetClient(options.Machines ?? [MachineSpec.Local(options.HostRoot)], options.Fleet ?? new FleetOptions { ClientName = DefaultClientName, SshTransportAdapter = new IrohSshTransport() });
            LocalEndpointToken? accessToken = null;
            var machines = fleet;
            app.Use(async (context, next) =>
            {
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers.XFrameOptions = "DENY";
                context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
                context.Response.Headers.CacheControl = "no-store";
                if (!WebAccessGate.IsAllowedHost(context.Request.Host.Value))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next(context).ConfigureAwait(false);
            });
            app.UseAuthentication();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
            app.Run(async context =>
            {
                var request = context.Request;
                var response = context.Response;
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
                var cancellationToken = lifetime.Token;
                if (request.Path == LoginPath && HttpMethods.IsPost(request.Method))
                {
                    if (!WebAccessGate.IsSameOrigin(request.Headers.Origin.ToString(), request.Host.Value))
                    {
                        response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                    if (!request.HasFormContentType || accessToken is null || !accessToken.Matches((await request.ReadFormAsync(cancellationToken).ConfigureAwait(false))["token"].ToString().Trim()))
                    {
                        response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                    var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "browser")], Scheme);
                    await context.SignInAsync(Scheme, new ClaimsPrincipal(identity)).ConfigureAwait(false);
                    response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }
                if (!HttpMethods.IsGet(request.Method))
                {
                    response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    return;
                }
                if (request.Path == WebSocketPath && !WebAccessGate.IsSameOrigin(request.Headers.Origin.ToString(), request.Host.Value))
                {
                    response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (request.Path == SessionPath || request.Path == MachinesPath || request.Path == WebSocketPath)
                {
                    if (context.User.Identity?.IsAuthenticated != true)
                    {
                        response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                    if (request.Path == SessionPath)
                    {
                        response.StatusCode = StatusCodes.Status204NoContent;
                        return;
                    }
                    if (request.Path == MachinesPath)
                    {
                        IReadOnlyList<FleetMachineStatus> statuses = [.. machines.Statuses().Zip(machines.Machines, (status, connection) => CredentialRedaction.Redact(status, connection.Machine))];
                        response.ContentType = "application/json";
                        await JsonSerializer.SerializeAsync(response.Body, statuses, WebBridgeJsonContext.Default.IReadOnlyListFleetMachineStatus, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (!context.WebSockets.IsWebSocketRequest)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }
                    var name = request.Query[MachineParameter].ToString();
                    var machine = name.Length == 0 ? machines.Machines[0] : machines.Find(name);
                    if (machine is null)
                    {
                        response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }
                    using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                    await HostRelay.RunAsync(socket, machine, reason => CredentialRedaction.Redact(reason, machine.Machine), cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (await StaticAssets.LoadAsync(request.Path.Value ?? "/", cancellationToken).ConfigureAwait(false) is not { } asset)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                response.ContentType = asset.ContentType;
                await response.Body.WriteAsync(asset.Content, cancellationToken).ConfigureAwait(false);
            });
            await app.StartAsync().ConfigureAwait(false);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var port = new Uri(addresses.Addresses.Single()).Port;
            token = LocalEndpointToken.Issue(portFile);
            accessToken = token;
            OwnerOnlyFiles.Write(portFile, port.ToString(CultureInfo.InvariantCulture));
            fleet.Start();
            return new WebBridgeServer(app, token, portFile, fleet, port, suppliedFleet is null);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            if (fleet is not null && suppliedFleet is null)
                await fleet.DisposeAsync().ConfigureAwait(false);
            token?.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        if (_ownsFleet)
            await _fleet.DisposeAsync().ConfigureAwait(false);
        try { File.Delete(_portFile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        _token.Dispose();
    }
}
