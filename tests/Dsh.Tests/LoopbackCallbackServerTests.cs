using System.Net.Sockets;
using System.Text;
using Dsh.Account;

namespace Dsh.Tests;

public sealed class LoopbackCallbackServerTests
{
    [Fact]
    public async Task AcceptsValidCallback_AndReturnsCode()
    {
        await using var server = new LoopbackCallbackServer();
        server.Start();
        var state = AccountPkce.NewState();

        var wait = server.WaitForCodeAsync(state, TestContext.Current.CancellationToken);
        var response = await SendAsync($"{server.RedirectUri}?code=abc&state={state}");

        Assert.Contains("200 OK", response);
        Assert.Equal("abc", await wait);
    }

    [Fact]
    public async Task RejectsWrongStateWrongMethodAndDuplicateCode()
    {
        await using var server = new LoopbackCallbackServer();
        server.Start();
        var state = AccountPkce.NewState();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wait = server.WaitForCodeAsync(state, timeout.Token);

        Assert.Contains("400", await SendAsync($"{server.RedirectUri}?code=abc&state=wrong"));
        Assert.Contains("405", await SendAsync(server.RedirectUri, method: "POST"));
        Assert.Contains("400", await SendAsync($"{server.RedirectUri}?code=a&code=b&state={state}"));

        timeout.Cancel();
        Assert.Null(await wait);
    }

    private static async Task<string> SendAsync(string url, string method = "GET")
    {
        var uri = new Uri(url);
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port, TestContext.Current.CancellationToken);
        await using var stream = client.GetStream();
        var request = $"{method} {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
