using Cordis;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Web.Plugin.Web)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebFetchHttp)]
[assembly: DshPlugin(Dsh.Web.Plugin.WebSearchDeepseek)]
[assembly: DshPlugin(Dsh.Web.Plugin.ToolWeb)]

namespace Dsh.Web;

public sealed class Plugin(string packageName) : IDshPlugin
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
}