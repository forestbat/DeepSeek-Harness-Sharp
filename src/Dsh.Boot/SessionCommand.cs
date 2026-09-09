using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Boot;

public static class SessionCommand
{
    public static IDisposable Register(Context ctx, ISessionPersistence persistence)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        var session = commands.Register(new CommandDefinition
        {
            Name = "session",
            Description = "List or delete persistent sessions",
            Input = new CommandInputDescriptor("list | delete <id>"),
            MenuSchema = new CommandMenuSchema("Persistent sessions", "session id or title"),
            ArgumentSchemas = [new CommandArgumentSchema("session", "select", "Session id or title")],
            Handler = invocation => HandleSession(invocation, persistence, ctx)
        });
        var rename = commands.Register(new CommandDefinition
        {
            Name = "rename",
            Description = "Rename the current session",
            Input = new CommandInputDescriptor("<title>"),
            Handler = invocation => HandleRename(invocation, persistence)
        });
        return new CompositeDisposable(session, rename);
    }

    private static Task<CommandResult> HandleSession(CommandInvocation invocation, ISessionPersistence persistence, Context ctx)
    {
        var raw = invocation.RawInput.Trim();
        var parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            var sessions = persistence.List();
            if (sessions.Count == 0)
                return Task.FromResult<CommandResult>(new CommandResult.Success("no sessions"));
            return Task.FromResult<CommandResult>(new CommandResult.Success(
                string.Join('\n', sessions.Select(FormatSnapshot))));
        }

        if (string.Equals(parts[0], "delete", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 2)
            {
                var deletable = persistence.List()
                    .Where(snapshot => snapshot.Header.Id != invocation.Agent.Id)
                    .Select(FormatSnapshot)
                    .ToList();
                return Task.FromResult<CommandResult>(new CommandResult.Success(
                    deletable.Count == 0 ? "no deletable sessions" : string.Join('\n', deletable)));
            }

            var id = SessionId.Create(parts[1]);
            var snapshot = persistence.Stat(id);
            if (snapshot is null)
                return Task.FromResult<CommandResult>(new CommandResult.Error($"session not found: {id}"));
            if (id == invocation.Agent.Id)
                return Task.FromResult<CommandResult>(new CommandResult.Error("cannot delete the current session"));
            persistence.Delete(id);
            ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)?.Remove(id);
            return Task.FromResult<CommandResult>(new CommandResult.Success($"deleted session {id}"));
        }

        var match = persistence.List().FirstOrDefault(snapshot =>
            string.Equals(snapshot.Header.Id.Value, parts[0], StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.Header.Title, parts[0], StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<CommandResult>(match is null
            ? new CommandResult.Error($"session not found: {parts[0]}")
            : new CommandResult.Success(FormatSnapshot(match)));
    }

    private static Task<CommandResult> HandleRename(CommandInvocation invocation, ISessionPersistence persistence)
    {
        var title = invocation.RawInput.Trim();
        if (title.Length == 0)
            return Task.FromResult<CommandResult>(new CommandResult.Error("usage: /rename <title>"));
        persistence.Rename(invocation.Agent.Id, title);
        invocation.Agent.Session.Rename(title);
        return Task.FromResult<CommandResult>(new CommandResult.Success($"renamed session to \"{title}\""));
    }

    private static string FormatSnapshot(SessionPersistenceSnapshot snapshot)
    {
        var label = string.IsNullOrWhiteSpace(snapshot.Header.Title)
            ? snapshot.Header.Id.Value
            : $"{snapshot.Header.Id.Value}: {snapshot.Header.Title}";
        return $"  {label}";
    }

    private sealed class CompositeDisposable(params IDisposable[] disposables) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
