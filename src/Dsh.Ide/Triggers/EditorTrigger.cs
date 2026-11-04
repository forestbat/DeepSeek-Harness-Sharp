using System.Globalization;
using Dsh.Core;

namespace Dsh.Ide.Triggers;

public enum EditorTriggerMode { First, All }
public enum EditorTriggerOnce { Process, Workspace, Session, Turn }

public sealed record EditorTriggerConditions
{
    public EditorConditions Target { get; init; } = new();
    public string[] ProjectTypes { get; init; } = [];
    public string[] MachineTypes { get; init; } = [];
    public string[] HostEditors { get; init; } = [];
    public string[] SessionSources { get; init; } = [];
}

public sealed record EditorTrigger
{
    public const int DefaultWriteCooldownSeconds = 60;
    public required string Id { get; init; }
    public required string On { get; init; }
    public required EditorAction Action { get; init; }
    public string[] Editors { get; init; } = ["auto"];
    public EditorTriggerConditions When { get; init; } = new();
    public EditorTriggerMode Mode { get; init; }
    public EditorTriggerOnce Once { get; init; } = EditorTriggerOnce.Session;
    public TimeSpan Cooldown { get; init; }
    public EditorFocus Focus { get; init; } = EditorFocus.Never;
    public bool StopOnSuccess { get; init; } = true;
    public bool Enabled { get; init; } = true;

    public static IReadOnlyList<EditorTrigger> Parse(object? value)
    {
        if (value is null)
            return [];
        if (value is not IEnumerable<object?> items || value is string)
            throw new ArgumentException("IDE triggers must be a list.");
        var rules = items.Select((item, index) => ParseRule(item, index)).ToArray();
        if (rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != rules.Length)
            throw new ArgumentException("IDE trigger IDs must be unique.");
        return rules;
    }

    private static EditorTrigger ParseRule(object? value, int index)
    {
        if (value is not IReadOnlyDictionary<string, object?> map)
            throw new ArgumentException("Each IDE trigger must be a mapping.");
        var on = ReadString(map, "on") ?? throw new ArgumentException("IDE trigger requires on.");
        if (on is not ("session.start" or "session.resume" or "tool.after_write" or "agent.error" or "test.failure" or "manual"))
            throw new ArgumentException($"Unknown IDE trigger event '{on}'.");
        var action = ReadString(map, "action") switch
        {
            "open_workspace" => EditorAction.OpenWorkspace,
            "open_file" => EditorAction.OpenFile,
            "open_diff" => EditorAction.OpenDiff,
            "reveal" => EditorAction.Reveal,
            _ => throw new ArgumentException("IDE trigger requires a supported action."),
        };
        return ReadPolicy(map, on, action, index);
    }

    private static EditorTrigger ReadPolicy(IReadOnlyDictionary<string, object?> map, string on, EditorAction action, int index)
        => new()
        {
            Id = ReadString(map, "id") ?? $"rule-{index + 1}",
            On = on,
            Action = action,
            Editors = ReadStrings(map, "editors") is { Length: > 0 } editors ? editors : ["auto"],
            When = ReadConditions(map.GetValueOrDefault("when")),
            Mode = ReadString(map, "mode") switch { null or "first" => EditorTriggerMode.First, "all" => EditorTriggerMode.All, _ => throw new ArgumentException("IDE trigger mode must be first or all.") },
            Once = ReadOnce(ReadString(map, "once"), on),
            Cooldown = ReadCooldown(map.GetValueOrDefault("cooldown"), on),
            Focus = ReadString(map, "focus") switch { null or "never" => EditorFocus.Never, "if_running" => EditorFocus.IfRunning, "always" => EditorFocus.Always, _ => throw new ArgumentException("Unknown IDE trigger focus policy.") },
            StopOnSuccess = ReadBoolean(map, "stop_on_success", true),
            Enabled = ReadBoolean(map, "enabled", true),
        };

    private static EditorTriggerConditions ReadConditions(object? value)
    {
        if (value is null)
            return new();
        if (value is not IReadOnlyDictionary<string, object?> map)
            throw new ArgumentException("IDE trigger when must be a mapping.");
        return new()
        {
            Target = new() { Workspace = ReadStrings(map, "workspace"), Extensions = ReadStrings(map, "extensions"), Globs = ReadStrings(map, "globs") },
            ProjectTypes = ReadStrings(map, "project_types"),
            MachineTypes = ReadStrings(map, "machine_types"),
            HostEditors = ReadStrings(map, "host_editors"),
            SessionSources = ReadStrings(map, "session_sources"),
        };
    }

    private static EditorTriggerOnce ReadOnce(string? value, string on) => value switch
    {
        null when on == "session.start" => EditorTriggerOnce.Workspace,
        null when on == "tool.after_write" => EditorTriggerOnce.Turn,
        null or "session" => EditorTriggerOnce.Session,
        "process" => EditorTriggerOnce.Process,
        "workspace" => EditorTriggerOnce.Workspace,
        "turn" => EditorTriggerOnce.Turn,
        _ => throw new ArgumentException("Unknown IDE trigger once scope."),
    };

    private static TimeSpan ReadCooldown(object? value, string on)
    {
        if (value is null)
            return on == "tool.after_write" ? TimeSpan.FromSeconds(DefaultWriteCooldownSeconds) : TimeSpan.Zero;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var multiplier = text.EndsWith("ms", StringComparison.Ordinal) ? 0.001 : text.EndsWith('m') ? 60 : 1;
        var number = text.EndsWith("ms", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('s') || text.EndsWith('m') ? text[..^1] : text;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || !double.IsFinite(duration) || duration < 0)
            throw new ArgumentException("IDE cooldown must be a nonnegative duration in ms, s or m.");
        return TimeSpan.FromSeconds(duration * multiplier);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var value) && value is string text ? text : null;

    private static string[] ReadStrings(IReadOnlyDictionary<string, object?> map, string key)
        => map.GetValueOrDefault(key) switch
        {
            null => [],
            string text => [text],
            IEnumerable<object?> items => items.Select(item => item as string ?? throw new ArgumentException($"IDE condition '{key}' must contain strings.")).ToArray(),
            _ => throw new ArgumentException($"IDE condition '{key}' must be a string or list."),
        };

    private static bool ReadBoolean(IReadOnlyDictionary<string, object?> map, string key, bool fallback)
        => map.GetValueOrDefault(key) switch { null => fallback, bool value => value, _ => throw new ArgumentException($"IDE trigger '{key}' must be boolean.") };
}
