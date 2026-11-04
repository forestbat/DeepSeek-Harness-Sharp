using Dsh.Core;
using Dsh.Ide.Selection;

namespace Dsh.Ide.Triggers;

public sealed record EditorTriggerContext(string Event, EditorRequest Request)
{
    public string MachineType { get; init; } = "local";
    public string SessionSource { get; init; } = "";
    public string[] ProjectTypes { get; init; } = [];
}

public sealed record EditorTriggerOutcome(string Rule, bool Executed, string Reason, IReadOnlyList<EditorResult> Results);

public sealed class EditorTriggerEngine(IEditorService editors, IReadOnlyList<EditorTrigger> rules, TimeProvider? clock = null, IReadOnlyList<EditorHostRule>? hostDetection = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IReadOnlyList<EditorHostRule> hostDetection = hostDetection ?? [];
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<(string Rule, string Scope), string> _completed = [];
    private readonly Dictionary<(EditorAction Action, string Path, string Editor), DateTimeOffset> _cooldown = [];
    private readonly Dictionary<string, DateTimeOffset> _ruleCooldown = new(StringComparer.Ordinal);

    public async ValueTask<IReadOnlyList<EditorTriggerOutcome>> ExecuteAsync(EditorTriggerContext context, CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workspace = editors.InspectWorkspace(context.Request.WorkspaceRoot);
            if (workspace.TriggersEnabled == false)
                return [];
            var host = context.Request.HostEditor ?? EditorSelection.DetectHostEditor(hostDetection);
            if (host != context.Request.HostEditor)
                context = context with { Request = context.Request with { HostEditor = host } };
            if (context.ProjectTypes.Length == 0 && rules.Any(rule => rule.When.ProjectTypes.Length > 0))
                context = context with { ProjectTypes = DetectProjectTypes(workspace) };
            RetireCooldowns();
            return await ExecuteRulesAsync(context, workspace, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async ValueTask<IReadOnlyList<EditorTriggerOutcome>> ExecuteRulesAsync(EditorTriggerContext context, EditorWorkspace workspace, CancellationToken cancellationToken)
    {
        var outcomes = new List<EditorTriggerOutcome>();
        foreach (var rule in OrderedRules(workspace))
        {
            if (!rule.Enabled || rule.On != context.Event || !Matches(rule.When, context, workspace))
                continue;
            if (_ruleCooldown.GetValueOrDefault(rule.Id) > _clock.GetUtcNow())
            {
                outcomes.Add(new(rule.Id, false, "cooldown", []));
                continue;
            }
            var (scope, value) = Scope(rule, context.Request);
            if (_completed.GetValueOrDefault((rule.Id, scope)) == value)
            {
                outcomes.Add(new(rule.Id, false, "once", []));
                continue;
            }
            var results = await ExecuteRuleAsync(rule, context.Request, cancellationToken).ConfigureAwait(false);
            var success = results.Any(result => result.Success);
            outcomes.Add(new(rule.Id, results.Count > 0, success ? "success" : "no successful candidate", results));
            if (!success)
                continue;
            if (!context.Request.DryRun && !context.Request.Print && results.Any(result => result.Success && !result.Printed))
            {
                _completed[(rule.Id, scope)] = value;
                _ruleCooldown[rule.Id] = _clock.GetUtcNow() + rule.Cooldown;
            }
            if (rule.StopOnSuccess)
                break;
        }
        return outcomes;
    }

    private IEnumerable<EditorTrigger> OrderedRules(EditorWorkspace workspace)
    {
        if (workspace.TriggerOrder.Length == 0)
            return rules;
        var order = workspace.TriggerOrder.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        return rules.OrderBy(rule => order.GetValueOrDefault(rule.Id, int.MaxValue)).ToArray();
    }

    private async ValueTask<IReadOnlyList<EditorResult>> ExecuteRuleAsync(EditorTrigger rule, EditorRequest original, CancellationToken cancellationToken)
    {
        var action = rule.Action == EditorAction.OpenFile && original.Action == EditorAction.OpenAtPosition ? EditorAction.OpenAtPosition : rule.Action;
        var request = original with { Action = action, Path = action == EditorAction.OpenWorkspace ? original.WorkspaceRoot : original.Path, TriggerId = rule.Id, TriggerEditors = rule.Editors, Focus = rule.Focus };
        var target = Path.GetFullPath(request.Path ?? request.WorkspaceRoot, request.WorkspaceRoot);
        var excluded = _cooldown.Where(item => item.Key.Action == request.Action && item.Key.Path == target && item.Value > _clock.GetUtcNow()).Select(item => item.Key.Editor).ToArray();
        request = request with { ExcludedEditors = excluded };
        var results = new List<EditorResult>();
        if (rule.Mode == EditorTriggerMode.First)
            results.Add(await editors.LaunchAsync(request, cancellationToken).ConfigureAwait(false));
        else
            await ExecuteAllAsync(request, results, cancellationToken).ConfigureAwait(false);
        if (!request.DryRun)
            foreach (var result in results.Where(result => result.Success && !result.Printed && result.Editor is not null))
                _cooldown[(request.Action, target, result.Editor!.Value.Value)] = _clock.GetUtcNow() + rule.Cooldown;
        return results;
    }

    private async ValueTask ExecuteAllAsync(EditorRequest request, List<EditorResult> results, CancellationToken cancellationToken)
    {
        var candidates = request.Editor is { } explicitEditor ? [new EditorId(explicitEditor)]
            : request.TriggerEditors.Contains("auto", StringComparer.Ordinal)
                ? (await editors.LaunchAsync(request with { DryRun = true }, cancellationToken).ConfigureAwait(false)).Decision.CandidateOrder
                : request.TriggerEditors.Select(id => new EditorId(id)).Distinct().ToArray();
        foreach (var editor in candidates)
        {
            if (request.ExcludedEditors.Contains(editor.Value, StringComparer.Ordinal))
                continue;
            var candidate = request with { Editor = editor.Value };
            var preview = await editors.LaunchAsync(candidate with { DryRun = true }, cancellationToken).ConfigureAwait(false);
            if (!preview.Success)
                continue;
            results.Add(request.DryRun ? preview : await editors.LaunchAsync(candidate, cancellationToken).ConfigureAwait(false));
        }
    }

    private static string[] DetectProjectTypes(EditorWorkspace workspace)
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var marker in workspace.Markers)
        {
            var type = marker.ToLowerInvariant() switch
            {
                "package.json" => "node", "cargo.toml" => "rust", "go.mod" => "go",
                "pyproject.toml" or "setup.py" or "requirements.txt" => "python",
                "pom.xml" or "build.gradle" or "build.gradle.kts" => "java",
                "cmakelists.txt" or "meson.build" or "makefile" => "cpp",
                _ => Path.GetExtension(marker).ToLowerInvariant() switch
                {
                    ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj" => "dotnet",
                    ".iml" => "java", ".cbp" => "cpp", _ => null,
                },
            };
            if (type is not null)
                types.Add(type);
        }
        return types.ToArray();
    }

    private static bool Matches(EditorTriggerConditions when, EditorTriggerContext context, EditorWorkspace workspace)
        => EditorSelection.MatchesAll(when.Target, workspace, context.Request.Path)
           && ContainsOrEmpty(when.MachineTypes, context.MachineType)
           && ContainsOrEmpty(when.HostEditors, context.Request.HostEditor ?? "")
           && ContainsOrEmpty(when.SessionSources, context.SessionSource)
           && (when.ProjectTypes.Length == 0 || when.ProjectTypes.Intersect(context.ProjectTypes, StringComparer.OrdinalIgnoreCase).Any());

    private static bool ContainsOrEmpty(string[] values, string value)
        => values.Length == 0 || values.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static (string Scope, string Value) Scope(EditorTrigger rule, EditorRequest request) => rule.Once switch
    {
        EditorTriggerOnce.Process => ("process", "completed"),
        EditorTriggerOnce.Workspace => ($"workspace:{request.WorkspaceRoot}", "completed"),
        EditorTriggerOnce.Turn => ($"session:{request.SessionId ?? request.WorkspaceRoot}", request.TurnId ?? "0"),
        _ => ($"session:{request.SessionId ?? request.WorkspaceRoot}", "completed"),
    };

    private void RetireCooldowns()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _cooldown.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            _cooldown.Remove(key);
    }

    public async ValueTask ForgetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var key in _completed.Keys.Where(key => key.Scope == $"session:{sessionId}").ToArray())
                _completed.Remove(key);
        }
        finally
        {
            _serial.Release();
        }
    }
}
