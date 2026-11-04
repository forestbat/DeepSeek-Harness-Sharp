using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Boot;
using Dsh.Gui.Services;
using Dsh.Runtime.Composition;
using YamlDotNet.Serialization;

namespace Dsh.Gui.ViewModels;

public sealed partial class EditorSettingsViewModel : ObservableObject
{
    public const string Package = "@deepseek-ai/dsh-ide";
    private readonly SettingsFacade _settings;
    private Dictionary<string, object?> _history = [];

    public EditorSettingsViewModel(SettingsFacade settings)
    {
        _settings = settings;
        Reload();
    }

    public ObservableCollection<EditorConfigurationRow> Editors { get; } = [];
    public ObservableCollection<EditorConfigurationRow> Triggers { get; } = [];
    public string[] FocusOptions { get; } = ["never", "if_running", "always"];
    public string[] ReuseOptions { get; } = ["auto", "always", "never"];
    public string[] FallbackOptions { get; } = ["none", "os-default"];
    public string[] KindOptions { get; } = ["", "gui", "terminal", "hybrid"];
    public string[] EventOptions { get; } = ["session.start", "session.resume", "tool.after_write", "agent.error", "test.failure", "manual"];
    public string[] ActionOptions { get; } = ["open_workspace", "open_file", "open_diff", "reveal"];
    public string[] ModeOptions { get; } = ["first", "all"];
    public string[] OnceOptions { get; } = ["process", "workspace", "session", "turn"];
    [ObservableProperty] private EditorConfigurationRow? _selectedEditor;
    [ObservableProperty] private EditorConfigurationRow? _selectedTrigger;
    [ObservableProperty] private string _defaultEditor = "auto";
    [ObservableProperty] private bool _strictMatch;
    [ObservableProperty] private bool _allowOutsideWorkspace;
    [ObservableProperty] private string _focus = "never";
    [ObservableProperty] private string _reuseWindow = "auto";
    [ObservableProperty] private string _fallback = "none";
    [ObservableProperty] private string _historyProviders = "";
    [ObservableProperty] private bool _historyTimeline;
    [ObservableProperty] private int _historyLimit;
    [ObservableProperty] private string _status = "";

    [RelayCommand]
    public void Reload()
    {
        var supplied = _settings.Load().Plugins.GetValueOrDefault(Package)?.Parameters ?? [];
        var map = PluginSettingsSection.DefaultParameters(Package);
        var historyDefaults = map.GetValueOrDefault("history") as Dictionary<string, object?> ?? [];
        foreach (var (key, value) in supplied) map[key] = value;
        DefaultEditor = Text(map, "default_editor", "auto");
        StrictMatch = Flag(map, "strict_match");
        AllowOutsideWorkspace = Flag(map, "allow_outside_workspace");
        Focus = Text(map, "focus", "never");
        ReuseWindow = Text(map, "reuse_window", "auto");
        Fallback = Text(map, "fallback", "none");
        _history = supplied.GetValueOrDefault("history") is Dictionary<string, object?> history
            ? new(history, StringComparer.Ordinal) : [];
        var configuredProviders = _history.TryGetValue("providers", out var providerValue) ? providerValue : historyDefaults.GetValueOrDefault("providers");
        HistoryProviders = configuredProviders switch
        {
            string provider => provider,
            IEnumerable providers => string.Join(", ", providers.Cast<object>()),
            _ => "",
        };
        HistoryTimeline = _history.ContainsKey("timeline") ? Flag(_history, "timeline") : Flag(historyDefaults, "timeline");
        var historyLimit = _history.TryGetValue("timeline_limit", out var configuredLimit)
            ? configuredLimit : historyDefaults.GetValueOrDefault("timeline_limit");
        HistoryLimit = int.TryParse(historyLimit?.ToString(), out var limit) ? limit : Convert.ToInt32(historyDefaults.GetValueOrDefault("timeline_limit"));
        LoadRows(Editors, map.GetValueOrDefault("editors"));
        foreach (var row in Editors) row.EnsureKey("remote_executables", Array.Empty<string>());
        LoadRows(Triggers, map.GetValueOrDefault("triggers"));
        SelectedEditor = Editors.FirstOrDefault();
        SelectedTrigger = Triggers.FirstOrDefault();
    }

    [RelayCommand]
    public void Save()
    {
        try
        {
            var editors = Editors.Select(row => row.Read()).ToArray();
            var triggers = Triggers.Select(row => row.Read()).ToArray();
            if (editors.Any(map => string.IsNullOrWhiteSpace(Text(map, "id", ""))))
                throw new ArgumentException("Each editor requires an id.");
            if (editors.Select(map => Text(map, "id", "")).Distinct(StringComparer.Ordinal).Count() != editors.Length)
                throw new ArgumentException("Editor ids must be unique.");
            if (HistoryLimit < 1) throw new ArgumentException("History limit must be positive.");
            var history = new Dictionary<string, object?>(_history, StringComparer.Ordinal)
            {
                ["providers"] = HistoryProviders.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                ["timeline"] = HistoryTimeline,
                ["timeline_limit"] = HistoryLimit,
            };
            _settings.SavePluginParameters(Package, new Dictionary<string, object?>
            {
                ["editors"] = editors, ["triggers"] = triggers, ["default_editor"] = DefaultEditor,
                ["strict_match"] = StrictMatch, ["allow_outside_workspace"] = AllowOutsideWorkspace,
                ["focus"] = Focus, ["reuse_window"] = ReuseWindow, ["fallback"] = Fallback, ["history"] = history,
            });
            Status = "IDE settings saved. Restart DSH or reload the IDE plugin to apply the new configuration.";
        }
        catch (Exception error) when (error is ArgumentException or YamlDotNet.Core.YamlException)
        {
            Status = error.Message;
        }
    }

    [RelayCommand] private void AddEditor()
    {
        var row = new EditorConfigurationRow(new Dictionary<string, object?>
        {
            ["id"] = $"custom-{Editors.Count + 1}", ["enabled"] = true,
            ["kind"] = "gui", ["command"] = "",
            ["remote_executables"] = Array.Empty<string>(),
            ["actions"] = new Dictionary<string, object?> { ["open_file"] = new[] { "{path}" }, ["open_workspace"] = new[] { "{workspace}" } },
            ["when"] = new Dictionary<string, object?> { ["workspace"] = Array.Empty<string>(), ["extensions"] = Array.Empty<string>(), ["globs"] = Array.Empty<string>() },
            ["env"] = new Dictionary<string, object?>(),
        });
        Editors.Add(row); SelectedEditor = row;
    }
    [RelayCommand] private void AddTrigger()
    {
        var row = new EditorConfigurationRow(new Dictionary<string, object?>
        {
            ["on"] = "manual", ["action"] = "open_file", ["editors"] = new[] { "auto" },
            ["enabled"] = true, ["mode"] = "first", ["once"] = "session", ["cooldown"] = "0s",
            ["focus"] = "never", ["stop_on_success"] = true, ["when"] = new Dictionary<string, object?>(),
        });
        Triggers.Add(row); SelectedTrigger = row;
    }
    [RelayCommand] private void RemoveEditor() { if (SelectedEditor is { } row) Editors.Remove(row); SelectedEditor = Editors.FirstOrDefault(); }
    [RelayCommand] private void RemoveTrigger() { if (SelectedTrigger is { } row) Triggers.Remove(row); SelectedTrigger = Triggers.FirstOrDefault(); }
    [RelayCommand] private void MoveEditorUp() => Move(Editors, SelectedEditor, -1);
    [RelayCommand] private void MoveEditorDown() => Move(Editors, SelectedEditor, 1);
    [RelayCommand] private void MoveTriggerUp() => Move(Triggers, SelectedTrigger, -1);
    [RelayCommand] private void MoveTriggerDown() => Move(Triggers, SelectedTrigger, 1);
    private static void Move(ObservableCollection<EditorConfigurationRow> rows, EditorConfigurationRow? row, int delta)
    {
        var index = row is null ? -1 : rows.IndexOf(row);
        if (index >= 0 && index + delta >= 0 && index + delta < rows.Count) rows.Move(index, index + delta);
    }
    private static void LoadRows(ObservableCollection<EditorConfigurationRow> rows, object? raw)
    {
        rows.Clear();
        if (raw is IEnumerable items and not string)
            foreach (var item in items)
                if (item is Dictionary<string, object?> map) rows.Add(new(map));
                else if (item is string id) rows.Add(new(new Dictionary<string, object?> { ["id"] = id }));
    }
    private static string Text(Dictionary<string, object?> map, string key, string fallback) => map.GetValueOrDefault(key) as string ?? fallback;
    private static bool Flag(Dictionary<string, object?> map, string key) => map.GetValueOrDefault(key) is true;
}

public sealed partial class EditorConfigurationRow : ObservableObject
{
    private static readonly ISerializer Serializer = new SerializerBuilder().Build();
    public EditorConfigurationRow(Dictionary<string, object?> map) => _yaml = Serializer.Serialize(map);
    [ObservableProperty] private string _yaml;
    public void EnsureKey(string key, object? value)
    {
        var map = Read();
        if (map.ContainsKey(key)) return;
        map[key] = value;
        Yaml = Serializer.Serialize(map);
    }
    public string Label
    {
        get
        {
            try { var map = Read(); return (map.GetValueOrDefault("id") ?? map.GetValueOrDefault("on") ?? "Rule").ToString()!; }
            catch (YamlDotNet.Core.YamlException) { return "Invalid YAML"; }
        }
    }
    public bool Enabled
    {
        get
        {
            try { return Read().GetValueOrDefault("enabled") is not false; }
            catch (YamlDotNet.Core.YamlException) { return false; }
        }
        set
        {
            var map = Read();
            map["enabled"] = value;
            Yaml = Serializer.Serialize(map);
        }
    }
    public string Id { get => GetText("id"); set => Set("id", value); }
    public string Command { get => GetText("command"); set => Set("command", value); }
    public string Kind { get => GetText("kind"); set => Set("kind", value); }
    public string Event { get => GetText("on", "manual"); set => Set("on", value); }
    public string Action { get => GetText("action", "open_file"); set => Set("action", value); }
    public string Mode { get => GetText("mode", "first"); set => Set("mode", value); }
    public string Once { get => GetText("once", "session"); set => Set("once", value); }
    public string Cooldown { get => GetText("cooldown", "0s"); set => Set("cooldown", value); }
    public string Focus { get => GetText("focus", "never"); set => Set("focus", value); }
    public string EditorOrder
    {
        get
        {
            try
            {
                return Read().GetValueOrDefault("editors") is IEnumerable items and not string
                    ? string.Join(", ", items.Cast<object>()) : GetText("editors", "auto");
            }
            catch (YamlDotNet.Core.YamlException) { return ""; }
        }
        set => Set("editors", value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
    public bool StopOnSuccess
    {
        get
        {
            try { return Read().GetValueOrDefault("stop_on_success") is not false; }
            catch (YamlDotNet.Core.YamlException) { return true; }
        }
        set => Set("stop_on_success", value);
    }
    private string GetText(string key, string fallback = "")
    {
        try { return Read().GetValueOrDefault(key)?.ToString() ?? fallback; }
        catch (YamlDotNet.Core.YamlException) { return fallback; }
    }
    private void Set(string key, object? value)
    {
        var map = Read();
        if (Equals(map.GetValueOrDefault(key), value)) return;
        if (key is "kind" or "command" && (value is null || value is string text && string.IsNullOrWhiteSpace(text)))
            map.Remove(key);
        else
            map[key] = value;
        Yaml = Serializer.Serialize(map);
    }
    partial void OnYamlChanged(string value)
    {
        OnPropertyChanged("");
    }
    public Dictionary<string, object?> Read() => YamlConfig.LoadMapping(Yaml);
}
