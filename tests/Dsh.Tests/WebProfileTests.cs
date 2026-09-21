using Dsh.Web;

namespace Dsh.Tests;

public sealed class WebProfileTests
{
    private static readonly HttpClient HttpClient = new();

    [Fact]
    public async Task WebProfile_ServesRootPageAndHealthEndpoint()
    {
        await using var server = new WebProfileServer();

        var root = await HttpClient.GetStringAsync($"http://127.0.0.1:{server.Port}/", TestContext.Current.CancellationToken);
        Assert.Contains("DeepSeek Harness", root);

        var health = await HttpClient.GetStringAsync($"http://127.0.0.1:{server.Port}/api/health", TestContext.Current.CancellationToken);
        Assert.Contains("\"ok\":true", health);

        await server.StopAsync();
    }
}
