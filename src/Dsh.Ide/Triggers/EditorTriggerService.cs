using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Dsh.Core;
using Dsh.Ide.Bridge;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Ide.Triggers;

public sealed partial class EditorTriggerService : IAsyncDisposable
{
    public const string ServiceName = "editorTriggers";
    private const int QueueCapacity = 64;
    private readonly Context _ctx;
    private readonly EditorTriggerEngine _engine;
    private readonly IReadOnlyList<EditorTrigger> _rules;
    private readonly Channel<EditorTriggerContext> _queue = Channel.CreateBounded<EditorTriggerContext>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<bool>[] _remove;
    private readonly Task _worker;
    private IReadOnlyList<EditorTriggerOutcome> _last = [];

    public EditorTriggerService(Context ctx, IEditorService editors, IReadOnlyList<EditorTrigger> rules, IReadOnlyList<EditorHostRule>? hostDetection = null)
    {
        _ctx = ctx;
        _rules = rules;
        _engine = new(editors, rules, hostDetection: hostDetection);
        var global = new EventOptions { Global = true };
        _remove = [
            ctx.On<AgentSessionStartNotification>(SessionStarted, global),
            ctx.On<SessionEventNotification>(FileChanged, global),
            ctx.On<ToolResultNotification>(ToolFinished, global),
            ctx.On<AgentErrorNotification>(AgentFailed, global),
            ctx.On<EditorTestFailureNotification>(TestFailed, global),
            ctx.OnAsync<SessionDisposedNotification>(notification => _engine.ForgetSessionAsync(notification.Session.Id.Value, _stop.Token), global),
        ];
        _worker = RunAsync();
    }

    public IReadOnlyList<EditorTrigger> Rules => _rules;
    public IReadOnlyList<EditorTriggerOutcome> LastOutcomes => Volatile.Read(ref _last);
    public async ValueTask<IReadOnlyList<EditorTriggerOutcome>> ExecuteAsync(EditorTriggerContext context, CancellationToken cancellationToken = default)
    {
        var outcomes = await _engine.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _last, outcomes);
        return outcomes;
    }

    private void SessionStarted(AgentSessionStartNotification notification)
    {
        var kind = notification.Source == "resume" ? "session.resume" : "session.start";
        _ctx.Get<EditorClientDispatcher>(EditorClientDispatcher.ServiceName, false)?.BindCurrentClient(notification.Agent.Id.Value);
        Enqueue(ContextFor(notification.Agent, kind, null, null, notification.Source));
    }

    private void ToolFinished(ToolResultNotification notification)
    {
        if (notification.Run.Agent is not { } agent)
            return;
        ObserveTestFailures(agent, notification.Result.Meta);
    }

    private void FileChanged(SessionEventNotification notification)
    {
        if (notification.Event.Data is FileChangedPayload changed
            && _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)?.Get(notification.Session.Id) is { } agent)
            Enqueue(ContextFor(agent, "tool.after_write", new(changed.Path), EditorSessionContext.CurrentTurn(_ctx, notification.Session), "tool"));
    }

    private void AgentFailed(AgentErrorNotification notification)
    {
        var locations = LocationsFromException(notification.Error);
        foreach (var location in locations)
            Enqueue(ContextFor(notification.Agent, "agent.error", location, notification.Turn, "agent"));
    }

    private void TestFailed(EditorTestFailureNotification notification)
    {
        foreach (var location in notification.Locations)
            Enqueue(ContextFor(notification.Agent, "test.failure", location, notification.Turn, "test"));
    }

    private void ObserveTestFailures(IAgent agent, JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } meta || !meta.TryGetProperty("testFailures", out var failures) || failures.ValueKind != JsonValueKind.Array)
            return;
        foreach (var failure in failures.EnumerateArray())
        {
            if (ReadLocation(failure) is { } location)
                Enqueue(ContextFor(agent, "test.failure", location, EditorSessionContext.CurrentTurn(_ctx, agent.Session), "test"));
        }
    }

    private EditorTriggerContext ContextFor(IAgent agent, string kind, EditorSourceLocation? location, int? turn, string source)
    {
        var workspace = agent.Session.WorkingDirectory ?? Environment.CurrentDirectory;
        return new(kind, new EditorRequest
        {
            WorkspaceRoot = workspace,
            Path = location?.Path,
            Action = location is null ? EditorAction.OpenWorkspace : EditorAction.OpenAtPosition,
            Line = location?.Line ?? 1,
            Column = location?.Column ?? 1,
            SessionId = agent.Id.Value,
            TurnId = (turn ?? EditorSessionContext.CurrentTurn(_ctx, agent.Session)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }) { SessionSource = source, MachineType = MachineTypeFor(agent.Id.Value) };
    }

    private string MachineTypeFor(string? sessionId)
        => _ctx.Get<EditorClientDispatcher>(EditorClientDispatcher.ServiceName, false)?.TryIsLocalMachine(sessionId) is { } local
            ? local ? "local" : "remote"
            : _ctx.Get<EditorExecutionContext>(EditorExecutionContext.ServiceName, false)?.IsRemoteHost == true ? "remote" : "local";


    private void Enqueue(EditorTriggerContext context)
    {
        if (_rules.All(rule => !rule.Enabled || rule.On != context.Event) || _stop.IsCancellationRequested)
            return;
        if (!_queue.Writer.TryWrite(context))
            _ctx.Logger.Warn("IDE trigger queue full; event=%s", context.Event);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var context in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await ExecuteQueuedAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteQueuedAsync(EditorTriggerContext context)
    {
        try
        {
            var outcomes = await ExecuteAsync(context, _stop.Token).ConfigureAwait(false);
            foreach (var outcome in outcomes)
                _ctx.Logger.Info("IDE trigger rule=%s executed=%s result=%s", outcome.Rule, outcome.Executed, outcome.Reason);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _ctx.Logger.Warn("IDE trigger failed; event=%s error=%s", context.Event, error.GetType().Name);
        }
    }


    private static EditorSourceLocation? ReadLocation(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
            return null;
        var line = value.TryGetProperty("line", out var lineValue) && lineValue.ValueKind == JsonValueKind.Number && lineValue.TryGetInt32(out var lineNumber) ? Math.Max(1, lineNumber) : 1;
        var column = value.TryGetProperty("column", out var columnValue) && columnValue.ValueKind == JsonValueKind.Number && columnValue.TryGetInt32(out var columnNumber) ? Math.Max(1, columnNumber) : 1;
        return new(path.GetString()!, line, column);
    }

    internal static IReadOnlyList<EditorSourceLocation> LocationsFromException(Exception exception)
    {
        var locations = new HashSet<EditorSourceLocation>();
        ReadExceptionLocations(exception.Message, locations);
        if (exception.StackTrace is { } trace)
            ReadExceptionLocations(trace, locations);
        return locations.ToArray();
    }

    private static void ReadExceptionLocations(string text, HashSet<EditorSourceLocation> locations)
    {
        foreach (Match match in StackLocation().Matches(text))
        {
            if (!int.TryParse(match.Groups["line"].Value, out var line))
                continue;
            var column = int.TryParse(match.Groups["column"].Value, out var parsedColumn) ? parsedColumn : 1;
            locations.Add(new(match.Groups["path"].Value.Trim(), Math.Max(1, line), Math.Max(1, column)));
        }
    }

    [GeneratedRegex("""(?:\bin\s+|^|\s)(?<path>(?:[A-Za-z]:[\\/]|/|\.?\.?[\\/])[^:\r\n]+?|[^\s:"'()]+\.[^\s:"'()]+?)(?::line\s+|:|\()(?<line>\d+)(?:[:,](?<column>\d+))?\)?(?:\s|:|$)""", RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.NonBacktracking)]
    private static partial Regex StackLocation();


    public async ValueTask DisposeAsync()
    {
        foreach (var remove in _remove)
            remove();
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
