using Dsh.Core;
using Dsh.Interaction;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.SessionQuery.Plugin.Service)]
[assembly: DshPlugin(Dsh.SessionQuery.Plugin.ToolSessionQuery)]

namespace Dsh.SessionQuery;

/** 会话检索:@deepseek-ai/dsh-session-query 提供检索服务并注册 /session-query 命令,
 *  @deepseek-ai/dsh-tool-session-query 暴露 session_search 工具。 */
public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Service = "@deepseek-ai/dsh-session-query";
    internal const string ToolSessionQuery = "@deepseek-ai/dsh-tool-session-query";

    public string[] Inject => packageName switch
    {
        Service => [CommandsService.ServiceName],
        ToolSessionQuery => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SessionQueryService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Service => ApplyService(ctx),
        ToolSessionQuery => ToolSessionQueryTool.Apply(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable ApplyService(Context ctx)
    {
        var service = new SessionQueryService(ctx);
        var command = SessionsCommand.Register(ctx, service);
        return new Bundle(service, command);
    }

    private sealed class Bundle(SessionQueryService service, IDisposable command) : IDisposable
    {
        public void Dispose()
        {
            command.Dispose();
            service.Dispose();
        }
    }
}
