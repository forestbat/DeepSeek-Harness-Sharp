using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Checkpoints;

public static class CheckpointCommand
{
    public const string FilesOnlyFlag = "--files";

    public static IDisposable Register(Context ctx, CheckpointService service)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)
            ?? throw new InvalidOperationException("checkpoints requires the commands service");
        return commands.Register(new CommandDefinition
        {
            Name = "checkpoints",
            Description = "List or restore shadow-git checkpoints of the project",
            Input = new CommandInputDescriptor("list | status | restore <index> [--files]"),
            Handler = invocation => ExecuteAsync(ctx, service, invocation),
        });
    }

    private static async Task<CommandResult> ExecuteAsync(Context ctx, CheckpointService service, CommandInvocation invocation)
    {
        var tokens = invocation.RawInput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cwd = invocation.Agent.Session.Header.Cwd;
        if (cwd is not { Length: > 0 })
            return new CommandResult.Error("the current session has no project directory");
        if (tokens.Length == 0 || tokens[0] == "list")
        {
            var points = service.PointsFor(cwd);
            if (points.Count == 0)
                return new CommandResult.Success("no checkpoints recorded for this project");
            var lines = points.Select((point, position) => CheckpointLog.FormatPoint(position, point));
            return new CommandResult.Success(string.Join('\n', lines));
        }
        if (tokens[0] == "status")
        {
            var points = service.PointsFor(cwd);
            var path = Path.Combine(
                new HarnessHome(ctx.GetProp("dshHomePath") as string ?? HarnessHome.Resolve().Root).CheckpointsPath,
                ProjectStorageKey.Of(cwd));
            return new CommandResult.Success(
                $"enabled: true\npoints: {points.Count}/{service.MaxPoints}\nkeep days: {service.KeepDays}\nstore: {path}");
        }
        if (tokens[0] != "restore" || tokens.Length < 2 || !int.TryParse(tokens[1], out var index))
            return new CommandResult.Error("usage: /checkpoints list | status | restore <index> [--files]");
        try
        {
            var commit = await service.RestoreFilesAsync(cwd, index, invocation.Signal);
            if (tokens.Contains(FilesOnlyFlag))
                return new CommandResult.Success($"files restored from {CheckpointLog.Shorten(commit)}");
            var forkId = ForkSession(ctx, invocation.Agent, service, index, cwd);
            return new CommandResult.Success(
                $"files restored from {CheckpointLog.Shorten(commit)}; session context forked to {forkId}: use /resume {forkId} to continue from that point");
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            return new CommandResult.Error(error.Message);
        }
    }

    private static string ForkSession(Context ctx, IAgent agent, CheckpointService service, int index, string cwd)
    {
        var persistence = ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)
            ?? throw new InvalidOperationException("session persistence is not available");
        var point = service.PointsFor(cwd)[index];
        var events = agent.Session.SnapshotEvents(0, point.Seq);
        if (events.Count == 0)
            throw new InvalidOperationException("checkpoint seq is beyond the current session log");
        var header = agent.Session.Header with
        {
            Id = SessionId.Create($"session-restore-{Guid.NewGuid():N}"),
            Title = $"restored from {CheckpointLog.Shorten(point.Commit)}",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ParentSession = agent.Session.Header.Id,
        };
        using var handle = persistence.Create(header);
        handle.Append(events);
        handle.Close();
        return header.Id.Value;
    }
}
