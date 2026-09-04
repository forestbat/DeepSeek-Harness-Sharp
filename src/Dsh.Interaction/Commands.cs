using System.Text.RegularExpressions;
using Cordis;
using Dsh.Core;

namespace Dsh.Interaction;

public sealed record CommandInputDescriptor(string Hint, bool Images = false);

public sealed record CommandDescriptor(string Name, string Description, CommandInputDescriptor? Input = null);

public abstract record CommandResult
{
    public sealed record Success(string? Text = null, long? SourceEventSeq = null) : CommandResult;

    public sealed record Error(string Text) : CommandResult;
}

public sealed record CommandExecution(string CommandId, CommandResult Result);

public sealed record ParsedCommand(string Name, string RawInput);

public sealed record CommandInvocation(
    string CommandId,
    IAgent Agent,
    string RawInput,
    CancellationToken Signal);

public sealed record CommandDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public CommandInputDescriptor? Input { get; init; }
    public bool RecordInput { get; init; } = true;
    public required Func<CommandInvocation, Task<CommandResult>> Handler { get; init; }
}

public static class CommandEvents
{
    public const string Run = "command/run";
    public const string Done = "command/done";
    public const string Change = "commands/change";
}

public sealed record CommandRunPayload(string CommandId, string Name, string? Args, string Source) : SessionEventPayload
{
    public override string Type => CommandEvents.Run;
}

public sealed record CommandDonePayload(string CommandId, string Kind, string? Text = null, long? SourceEventSeq = null)
    : SessionEventPayload
{
    public override string Type => CommandEvents.Done;
}

public sealed class CommandsService : Service
{
    public const string ServiceName = "commands";

    private static readonly Regex CommandName = new("^[a-z][a-z0-9_-]*$", RegexOptions.Compiled);
    private static readonly Regex CommandLine = new("""^/([a-z][a-z0-9_-]*)(?=$|[\t\n\r ])""", RegexOptions.Compiled);

    private sealed class CommandLayer
    {
        public NamedEntries<CommandDefinition> Commands { get; }

        public CommandLayer(ScopeKey? scope)
        {
            Commands = new NamedEntries<CommandDefinition>(name => new InvalidOperationException(scope is null
                ? $"command \"{name}\" is already registered (for a per-agent variant, register it under that agent's scope key)"
                : $"command \"{name}\" is already registered in this scope"));
        }
    }

    private readonly ScopedLayers<CommandLayer> _layers;
    private readonly string _instanceToken = Guid.NewGuid().ToString("N")[..8];
    private int _commandSeq;

    static CommandsService()
    {
        SessionEventCodec.Register<CommandRunPayload>(CommandEvents.Run);
        SessionEventCodec.Register<CommandDonePayload>(CommandEvents.Done);
    }

    public CommandsService(Context ctx) : base(ctx, ServiceName)
    {
        _layers = new ScopedLayers<CommandLayer>(scope => new CommandLayer(scope), () => ctx.Emit(CommandEvents.Change));
    }

    public static CommandsService Register(Context ctx) => new(ctx);

    public static ParsedCommand? ParseCommand(string line)
    {
        var match = CommandLine.Match(line);
        return match.Success ? new ParsedCommand(match.Groups[1].Value, line[match.Length..]) : null;
    }

    public IDisposable Register(CommandDefinition definition, ScopeKey? scope = null)
    {
        var normalized = NormalizeDefinition(definition);
        return _layers.Effect(Ctx, scope,
            layer => layer.Commands.Insert(normalized.Name, normalized),
            layer => layer.Commands.Remove(normalized.Name));
    }

    public IReadOnlyList<CommandDescriptor> List(IAgent agent)
        => _layers.Merge(agent.ScopeKey, layer => layer.Commands).Values
            .Select(definition => new CommandDescriptor(definition.Name, definition.Description, definition.Input))
            .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .ToList();

    public CommandDefinition? Find(IAgent agent, string name)
        => _layers.Merge(agent.ScopeKey, layer => layer.Commands).TryGetValue(name, out var definition)
            ? definition
            : null;

    public async Task<CommandExecution?> Execute(IAgent agent, string line, CancellationToken signal = default)
    {
        var parsed = ParseCommand(line);
        if (parsed is null)
            return null;
        var command = Find(agent, parsed.Name);
        if (command is null)
            return null;
        signal.ThrowIfCancellationRequested();
        var commandId = MintCommandId();
        agent.Session.Append(new CommandRunPayload(
            commandId, parsed.Name, command.RecordInput ? parsed.RawInput : null, "user"));
        CommandResult result;
        try
        {
            var invocation = new CommandInvocation(commandId, agent, parsed.RawInput, signal);
            result = NormalizeResult(parsed.Name, await RunHandler(command.Handler, invocation, signal));
        }
        catch (Exception error)
        {
            SettleThrown(agent.Session, parsed.Name, commandId, error);
            throw;
        }
        agent.Session.Append(DonePayload(commandId, result));
        return new CommandExecution(commandId, result);
    }

    private static async Task<CommandResult> RunHandler(
        Func<CommandInvocation, Task<CommandResult>> handler,
        CommandInvocation invocation,
        CancellationToken signal)
    {
        var running = handler(invocation);
        if (!signal.CanBeCanceled)
            return await running;
        var cancelled = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(
            static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null),
            cancelled);
        var completed = await Task.WhenAny(running, cancelled.Task);
        if (completed == cancelled.Task)
            throw new OperationCanceledException("command aborted", signal);
        return await running;
    }

    private void SettleThrown(Session session, string command, string commandId, Exception error)
    {
        try
        {
            session.Append(new CommandDonePayload(commandId, "error", error.Message));
        }
        catch (Exception appendError)
        {
            Ctx.LoggerFor(ServiceName).Warn($"command \"{command}\": command/done append failed: {appendError.Message}");
        }
    }

    private string MintCommandId() => $"cmd-{_instanceToken}-{Interlocked.Increment(ref _commandSeq)}";

    private static CommandDonePayload DonePayload(string commandId, CommandResult result)
        => result switch
        {
            CommandResult.Success success => new CommandDonePayload(commandId, "success", success.Text, success.SourceEventSeq),
            CommandResult.Error error => new CommandDonePayload(commandId, "error", error.Text),
            _ => throw new InvalidOperationException("unknown command result"),
        };

    private static CommandResult NormalizeResult(string command, CommandResult? result)
    {
        if (result is null)
            throw new InvalidOperationException($"command \"{command}\" handler must return a CommandResult");
        if (result is CommandResult.Error { Text: var text } && string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"command \"{command}\" error text must be a non-empty string");
        return result;
    }

    private static CommandDefinition NormalizeDefinition(CommandDefinition definition)
    {
        if (!CommandName.IsMatch(definition.Name))
            throw new ArgumentException($"command name \"{definition.Name}\" must match {CommandName}");
        if (string.IsNullOrWhiteSpace(definition.Description))
            throw new ArgumentException($"command \"{definition.Name}\" description must not be empty");
        if (definition.Input is { } input && string.IsNullOrWhiteSpace(input.Hint))
            throw new ArgumentException($"command \"{definition.Name}\" input hint must not be empty");
        return definition;
    }
}
