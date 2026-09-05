using Cordis;
using Dsh.Core;
using Dsh.Interaction;

namespace Dsh.Compaction;

public static class CompactCommand
{
    public const string Name = "compact";
    public const string Description = "Compact older conversation history";

    private const string Usage = "Usage: /compact (no arguments)";

    public static IDisposable Register(Context ctx)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)
            ?? throw new InvalidOperationException("command-compact requires the commands service");
        _ = ctx.Get<CompactionEngine>(CompactionEngine.ServiceName)
            ?? throw new InvalidOperationException("command-compact requires the compaction service");
        var gate = new Lock();
        var active = new List<Task>();
        var registration = commands.Register(new CommandDefinition
        {
            Name = Name,
            Description = Description,
            Handler = invocation =>
            {
                var operation = ExecuteCompact(ctx, invocation);
                lock (gate)
                    active.Add(operation);
                _ = operation.ContinueWith(
                    task =>
                    {
                        _ = task.Exception;
                        lock (gate)
                            active.Remove(task);
                    },
                    TaskContinuationOptions.ExecuteSynchronously);
                return operation;
            },
        });
        return new CompactCommandRegistration(() =>
        {
            registration.Dispose();
            Task[] pending;
            lock (gate)
                pending = [.. active];
            foreach (var task in pending)
            {
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch
                {
                    // Promise.allSettled semantics: teardown waits for settlement without re-reporting.
                }
            }
        });
    }

    private static async Task<CommandResult> ExecuteCompact(Context ctx, CommandInvocation invocation)
    {
        if (invocation.RawInput.Trim().Length > 0)
            return new CommandResult.Error(Usage);
        try
        {
            var compaction = ctx.Get<CompactionEngine>(CompactionEngine.ServiceName)!;
            var result = await compaction.CompactNow(invocation.Agent, invocation.Signal, invocation.CommandId);
            if (result is null)
                return new CommandResult.Success("No compactable history yet.");
            return new CommandResult.Success(
                $"Compacted {result.ShadowedSeqs.Count} history items (~{result.ShadowedTokenCount} tokens).",
                result.SummarySeq);
        }
        catch (Exception error)
        {
            if (invocation.Signal.IsCancellationRequested)
                return new CommandResult.Error("Compaction cancelled.");
            if (error is ManualCompactionError manual)
                return ExpectedFailure(manual);
            throw;
        }
    }

    private static CommandResult ExpectedFailure(ManualCompactionError error) => error.ErrorCode switch
    {
        ManualCompactionErrorCode.Busy => new CommandResult.Error(
            "Compaction is unavailable because this process has an active compaction, or the agent is not idle."),
        ManualCompactionErrorCode.Cancelled => new CommandResult.Error("Compaction cancelled."),
        ManualCompactionErrorCode.Changed => new CommandResult.Error(
            "The history selected for compaction changed before it could be replaced. The conversation is unchanged; the attempt is recorded in the session log."),
        ManualCompactionErrorCode.Summary => new CommandResult.Error(
            "Compaction could not produce a useful summary. The conversation is unchanged; the attempt is recorded in the session log."),
        ManualCompactionErrorCode.Commit => new CommandResult.Error(
            "Compaction did not finish cleanly; some session history may have changed. Inspect the current session state before retrying."),
        ManualCompactionErrorCode.Persistence => new CommandResult.Error(
            "Compaction finished, but the session could not be saved."),
        _ => throw new ArgumentOutOfRangeException(nameof(error), $"unknown manual compaction error code: {error.ErrorCode}"),
    };

    private sealed class CompactCommandRegistration(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }
}
