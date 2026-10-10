using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Web.Plugin.Web)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebFetchHttp)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebSearchDeepseek)]
[assembly: DshPlugin(Dsh.Web.Plugin.ToolWeb)]

namespace Dsh.Web;

/** Web 能力:运行时(web)、抓取提供方(http)、DeepSeek 搜索提供方与 web_search/web_fetch 工具;
 *  入口 "web" 启动 WebProfileServer。配置(如搜索 apiKey)写在 settings.yaml 的 plugins 段。 */
[DshEntrypoint("web")]
public sealed class Plugin(string packageName) : IDshPlugin, IDshEntrypoint
{
    internal const string Web = "@deepseek-ai/dsh-web";
    internal const string WebFetchHttp = "@deepseek-ai/dsh-web-fetch-http";
    internal const string WebSearchDeepseek = "@deepseek-ai/dsh-web-search-deepseek";
    internal const string ToolWeb = "@deepseek-ai/dsh-tool-web";

    public string[] Inject => packageName switch
    {
        Web => [],
        WebFetchHttp => [WebRuntime.ServiceName],
        WebSearchDeepseek => [WebRuntime.ServiceName],
        ToolWeb => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, WebRuntime.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Web => WebRuntime.Apply(ctx, config),
        WebFetchHttp => Dsh.Web.WebFetchHttp.Apply(ctx, config),
        WebSearchDeepseek => Dsh.Web.WebSearchDeepseek.Apply(ctx, config),
        ToolWeb => Dsh.Web.ToolWeb.Apply(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public async Task<int> RunAsync(HarnessApp app, PluginEntrypointOptions options, CancellationToken cancellationToken)
    {
        await using var server = new WebProfileServer();
        await Console.Out.WriteLineAsync($"dsharp web: http://127.0.0.1:{server.Port}");
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}
