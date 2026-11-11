using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Agent;

[assembly: DshPlugin(Dsh.Web.Plugin.Web)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebFetchHttp)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebSearchDeepseek)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebSearchGitHub)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebSearchStackOverflow)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebSearchBaidu)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebSearchGoogle)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebSearchBing)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebSearchDuckDuckGo)]
[assembly: DshPlugin(Dsh.Web.Plugin.ToolWeb)]

namespace Dsh.Web;

/** Web 工具能力:运行时、抓取和搜索提供方; 浏览器前端入口由 Dsh.WebBridge 提供。 */
public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Web = "@deepseek-ai/dsh-web";
    internal const string WebFetchHttp = "@deepseek-ai/dsh-web-fetch-http";
    internal const string WebSearchDeepseek = "@deepseek-ai/dsh-web-search-deepseek";
    internal const string WebSearchGitHub = "@deepseek-ai/dsh-web-search-github";
    internal const string WebSearchStackOverflow = "@deepseek-ai/dsh-web-search-stackoverflow";
    internal const string WebSearchBaidu = "@deepseek-ai/dsh-web-search-baidu";
    internal const string WebSearchGoogle = "@deepseek-ai/dsh-web-search-google";
    internal const string WebSearchBing = "@deepseek-ai/dsh-web-search-bing";
    internal const string WebSearchDuckDuckGo = "@deepseek-ai/dsh-web-search-duckduckgo";
    internal const string ToolWeb = "@deepseek-ai/dsh-tool-web";

    public string[] Inject => packageName switch
    {
        Web => [],
        WebFetchHttp => [WebRuntime.ServiceName],
        WebSearchDeepseek => [WebRuntime.ServiceName],
        WebSearchGitHub => [WebRuntime.ServiceName],
        WebSearchStackOverflow => [WebRuntime.ServiceName],
        WebSearchBaidu or WebSearchGoogle or WebSearchBing or WebSearchDuckDuckGo => [WebRuntime.ServiceName],
        ToolWeb => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, WebRuntime.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Web => WebRuntime.Apply(ctx, config),
        WebFetchHttp => Dsh.Web.WebFetchHttp.Apply(ctx, config),
        WebSearchDeepseek => Dsh.Web.WebSearchDeepseek.Apply(ctx, config),
        WebSearchGitHub => Dsh.Web.WebSearchGitHub.Apply(ctx, config),
        WebSearchStackOverflow => Dsh.Web.WebSearchStackOverflow.Apply(ctx, config),
        WebSearchBaidu => WebSearchGeneral.Apply(ctx, "baidu", config),
        WebSearchGoogle => WebSearchGeneral.Apply(ctx, "google", config),
        WebSearchBing => WebSearchGeneral.Apply(ctx, "bing", config),
        WebSearchDuckDuckGo => WebSearchGeneral.Apply(ctx, "duckduckgo", config),
        ToolWeb => Dsh.Web.ToolWeb.Apply(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

}
