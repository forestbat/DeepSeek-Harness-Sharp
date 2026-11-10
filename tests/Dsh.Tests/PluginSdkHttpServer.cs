using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dsh.Tests;

// A real loopback HTTP origin records requests; a large reply exercises both ABI buffer exchanges.
internal sealed class PluginSdkHttpServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private int _requests;

    private PluginSdkHttpServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _application = builder.Build();
        _application.MapPost("/payload", async context =>
        {
            Interlocked.Increment(ref _requests);
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            RequestBody = body.ToArray();
            RequestHeader = context.Request.Headers["X-Plugin"].ToString();
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.Headers["X-Plugin-Reply"] = "received";
            await context.Response.Body.WriteAsync(ResponseBody, context.RequestAborted);
        });
    }

    public byte[] ResponseBody { get; } = Enumerable.Repeat((byte)'x', 200_000).ToArray();
    public byte[]? RequestBody { get; private set; }
    public string? RequestHeader { get; private set; }
    public int Requests => Volatile.Read(ref _requests);
    public string Url { get; private set; } = "";

    public static async Task<PluginSdkHttpServer> StartAsync()
    {
        var server = new PluginSdkHttpServer();
        try
        {
            await server._application.StartAsync(TestContext.Current.CancellationToken);
            server.Url = server._application.Urls.Single() + "/payload";
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => _application.DisposeAsync();
}
