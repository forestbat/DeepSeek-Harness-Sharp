using Dsh.Interaction;
using Dsh.Runtime;

namespace Dsh.SessionQuery;

/** `/session-query <query>`:全文检索历史会话;命中可用 `/session <id>` 打开。 */
public static class SessionsCommand
{
    public static IDisposable Register(Context ctx, SessionQueryService service)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        return commands.Register(new CommandDefinition
        {
            Name = "session-query",
            Description = "Search session history",
            Input = new CommandInputDescriptor("<query>"),
            Handler = invocation => Task.FromResult(Handle(invocation, service)),
        });
    }

    private static CommandResult Handle(CommandInvocation invocation, SessionQueryService service)
    {
        var query = invocation.RawInput.Trim();
        if (query.Length == 0)
            return new CommandResult.Error("usage: /session-query <query>");
        var hits = service.Search(query);
        if (hits.Count == 0)
            return new CommandResult.Success($"no matches for \"{query}\"");
        var lines = hits.Select(hit => $"{hit.SessionId} [{hit.Type}] {hit.Snippet}");
        return new CommandResult.Success($"{string.Join('\n', lines)}\n(open one with /session <id>)");
    }
}
