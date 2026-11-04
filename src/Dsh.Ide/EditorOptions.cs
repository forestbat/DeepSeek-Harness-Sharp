using System.Collections;
using Dsh.Core;
using Dsh.Ide.History;
using Dsh.Ide.Bridge;
using Dsh.Boot;

namespace Dsh.Ide;

[global::Dsh.Plugins.DshPluginSettings]
public sealed record EditorOptions
{
    public IReadOnlyList<EditorDescriptor> Editors { get; init; } = [];
    [global::Dsh.Plugins.DshPluginSettings(Name = "host_detection")]
    public IReadOnlyList<EditorHostRule> HostDetection { get; init; } = [];
    [global::Dsh.Plugins.DshPluginSettings(Name = "installation_roots")]
    public IReadOnlyDictionary<string, string[]> InstallationRoots { get; init; } = new Dictionary<string, string[]>();
    [global::Dsh.Plugins.DshPluginSettings(Name = "bundle_roots")]
    public IReadOnlyList<string> BundleRoots { get; init; } = [];
    [global::Dsh.Plugins.DshPluginSettings(Name = "desktop_roots")]
    public IReadOnlyList<string> DesktopRoots { get; init; } = [];
    [global::Dsh.Plugins.DshPluginSettings(Name = "default_editor")]
    public string DefaultEditor { get; init; } = "auto";
    [global::Dsh.Plugins.DshPluginSettings(Name = "strict_match")]
    public bool StrictMatch { get; init; }
    [global::Dsh.Plugins.DshPluginSettings(Name = "reuse_window")]
    public EditorReuseWindow ReuseWindow { get; init; } = EditorReuseWindow.Auto;
    public EditorFocus Focus { get; init; } = EditorFocus.Never;
    public EditorFallback Fallback { get; init; }
    [global::Dsh.Plugins.DshPluginSettings(Name = "allow_outside_workspace")]
    public bool AllowOutsideWorkspace { get; init; }
    public IdeHistoryOptions History { get; init; } = new();
    public IReadOnlyList<Dsh.Ide.Triggers.EditorTrigger> Triggers { get; init; } = [];
    [global::Dsh.Plugins.DshPluginSettings(Name = "client_mappings")]
    public IReadOnlyList<EditorPathMapping> ClientMappings { get; init; } = [];
    public static EditorOptions From(object? config)
    {
        var supplied = ConfigMap.Read(config);
        var map = PluginSettingsSection.DefaultParameters(Plugin.Ide);
        var historyDefaults = map.GetValueOrDefault("history");
        var defaults = ConfigMap.Items(map.GetValueOrDefault("editors")).Where(IsEnabled).Select(item => ReadEditor(item, [])).ToArray();
        foreach (var (key, value) in supplied) map[key] = value;
        var editors = supplied.TryGetValue("editors", out var raw) ? ConfigMap.Items(raw).Where(IsEnabled).Select(item => ReadEditor(item, defaults)).ToArray() : defaults;
        return new EditorOptions
        {
            Editors = editors,
            HostDetection = ConfigMap.Items(map.GetValueOrDefault("host_detection")).Select(EditorHostRule.From).ToArray(),
            InstallationRoots = Catalog.EditorDiscoveryPaths.Read(map.GetValueOrDefault("installation_roots")),
            BundleRoots = ConfigMap.Strings(map.GetValueOrDefault("bundle_roots")),
            DesktopRoots = ConfigMap.Strings(map.GetValueOrDefault("desktop_roots")),
            DefaultEditor = ConfigMap.Text(map, "default_editor") ?? "auto",
            StrictMatch = ConfigMap.Boolean(map, "strict_match"),
            AllowOutsideWorkspace = ConfigMap.Boolean(map, "allow_outside_workspace"),
            ReuseWindow = ConfigMap.EnumValue(map, "reuse_window", EditorReuseWindow.Auto),
            Focus = ConfigMap.EnumValue(map, "focus", EditorFocus.Never),
            Fallback = ConfigMap.EnumValue(map, "fallback", EditorFallback.None),
            History = IdeHistoryOptions.From(config, historyDefaults),
            Triggers = Dsh.Ide.Triggers.EditorTrigger.Parse(map.GetValueOrDefault("triggers")),
            ClientMappings = EditorPathMapper.From(config).Mappings,
        };
    }

    private static bool IsEnabled(object? value)
    {
        var map = ConfigMap.Read(value);
        if (!map.TryGetValue("enabled", out var enabled)) return true;
        if (enabled is bool flag) return flag;
        if (enabled is string text && bool.TryParse(text, out var parsed)) return parsed;
        throw new ArgumentException("Editor enabled must be a boolean.");
    }

    private static EditorDescriptor ReadEditor(object? value, IReadOnlyList<EditorDescriptor> defaults)
    {
        if (value is string id)
            return defaults.FirstOrDefault(d => d.Id.Value == id) ?? throw new ArgumentException($"Unknown editor ID '{id}'.");
        var map = ConfigMap.Read(value);
        var name = ConfigMap.Text(map, "id") ?? throw new ArgumentException("Editor requires an ID.");
        var basis = defaults.FirstOrDefault(d => d.Id.Value == name);
        var command = ConfigMap.Text(map, "command") ?? basis?.Command ?? throw new ArgumentException($"Editor '{name}' requires a global command.");
        var actions = map.TryGetValue("actions", out var rawActions) ? ReadActions(rawActions) : basis?.Actions ?? new Dictionary<EditorAction, string[]>();
        var remoteActions = map.TryGetValue("remote_actions", out var rawRemoteActions) ? ReadActions(rawRemoteActions) : basis?.RemoteActions ?? new Dictionary<EditorAction, string[]>();
        var kind = ConfigMap.EnumValue(map, "kind", basis?.Kind ?? EditorKind.Gui);
        var conditions = map.TryGetValue("when", out var when) ? ConfigMap.Read(when) : null;
        var historyProvider = ConfigMap.EnumValue(map, "history_provider", basis?.HistoryProvider ?? IdeWorkspaceKind.None);
        var environment = map.TryGetValue("env", out var env) ? ConfigMap.Read(env).ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "") : basis?.Environment ?? new Dictionary<string, string>();
        return (basis ?? new EditorDescriptor { Id = name, Command = command }) with
        {
            DisplayName = ConfigMap.Text(map, "display_name") ?? basis?.DisplayName ?? name,
            Command = command, Kind = kind, Actions = actions, HistoryProvider = historyProvider,
            RemoteActions = remoteActions,
            DiscoveryCommands = map.TryGetValue("discovery_commands", out var discoveryCommands)
                ? ConfigMap.Items(discoveryCommands).Select(ReadDiscoveryCommand).ToArray() : basis?.DiscoveryCommands ?? [],
            ExecutableCandidates = map.TryGetValue("executables", out var executables) ? ConfigMap.Strings(executables) : ConfigMap.Text(map, "command") is not null ? [] : basis?.ExecutableCandidates ?? [],
            RemoteExecutableCandidates = map.TryGetValue("remote_executables", out var remoteExecutables) ? ConfigMap.Strings(remoteExecutables) : basis?.RemoteExecutableCandidates ?? [],
            BundleNames = map.TryGetValue("bundles", out var bundles) ? ConfigMap.Strings(bundles) : ConfigMap.Text(map, "command") is not null ? [] : basis?.BundleNames ?? [],
            Environment = environment, WorkingDirectory = ConfigMap.Text(map, "cwd") ?? basis?.WorkingDirectory,
            ReuseArguments = map.TryGetValue("reuse_args", out var reuse) ? ConfigMap.Strings(reuse) : basis?.ReuseArguments ?? [],
            NewWindowArguments = map.TryGetValue("new_window_args", out var newWindow) ? ConfigMap.Strings(newWindow) : basis?.NewWindowArguments ?? [],
            BackgroundArguments = map.TryGetValue("background_args", out var background) ? ConfigMap.Strings(background) : basis?.BackgroundArguments ?? [],
            FocusArguments = map.TryGetValue("focus_args", out var focus) ? ConfigMap.Strings(focus) : basis?.FocusArguments ?? [],
            Capabilities = actions.Keys.Aggregate(EditorCapability.None, (caps, action) => caps | EditorActionCapabilities.For(action))
                | (kind != EditorKind.Gui ? EditorCapability.Terminal : EditorCapability.None)
                | (historyProvider != IdeWorkspaceKind.None ? EditorCapability.History : EditorCapability.None)
                | (map.TryGetValue("remote", out _) ? ConfigMap.Boolean(map, "remote") ? EditorCapability.Remote : EditorCapability.None : basis?.Capabilities & EditorCapability.Remote ?? EditorCapability.None),
            When = conditions is null ? basis?.When ?? new() : new EditorConditions
            {
                Workspace = conditions.TryGetValue("workspace", out var markers) ? ConfigMap.Strings(markers) : [],
                Extensions = conditions.TryGetValue("extensions", out var extensions) ? ConfigMap.Strings(extensions) : [],
                Globs = conditions.TryGetValue("globs", out var globs) ? ConfigMap.Strings(globs) : [],
            },
        };
    }

    private static Dictionary<EditorAction, string[]> ReadActions(object? value)
    {
        var supplied = ConfigMap.Read(value);
        var actions = new Dictionary<EditorAction, string[]>();
        foreach (var (key, action) in supplied)
        {
            if (!Enum.TryParse<EditorAction>(key.Replace("_", ""), true, out var parsed) || !Enum.IsDefined(parsed))
                throw new ArgumentException($"Unknown editor action '{key}'.");
            actions[parsed] = ConfigMap.Strings(action);
        }
        if (actions.TryGetValue(EditorAction.OpenFile, out var file) && !actions.ContainsKey(EditorAction.OpenAtPosition)
            && file.Any(argument => argument.Contains("{line}", StringComparison.Ordinal)))
            actions[EditorAction.OpenAtPosition] = file;
        return actions;
    }

    private static EditorDiscoveryCommand ReadDiscoveryCommand(object? value)
    {
        var map = ConfigMap.Read(value);
        return new(
            ConfigMap.Text(map, "platform") ?? throw new ArgumentException("Discovery command requires a platform."),
            ConfigMap.Text(map, "command") ?? throw new ArgumentException("Discovery command requires an executable."),
            ConfigMap.Strings(map.GetValueOrDefault("args")));
    }
}

public sealed record EditorHostRule(EditorId Editor, string Variable, string Pattern)
{
    internal static EditorHostRule From(object? value)
    {
        var map = ConfigMap.Read(value);
        return new(
            ConfigMap.Text(map, "editor") ?? throw new ArgumentException("Host detection requires an editor ID."),
            ConfigMap.Text(map, "variable") ?? throw new ArgumentException("Host detection requires an environment variable name."),
            ConfigMap.Text(map, "pattern") ?? "*");
    }
}

internal static class ConfigMap
{
    public static Dictionary<string, object?> Read(object? value)
    {
        if (value is IDictionary<string, object?> typed)
            return new(typed, StringComparer.Ordinal);
        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
                if (entry.Key is string key) result[key] = entry.Value;
            return result;
        }
        return new(StringComparer.Ordinal);
    }
    public static IEnumerable<object?> Items(object? value) => value is IEnumerable list && value is not string ? list.Cast<object?>() : [];
    public static string[] Strings(object? value) => value is string text ? [text] : Items(value).OfType<string>().ToArray();
    public static string? Text(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) ? value as string : null;
    public static bool Boolean(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) && (value is true || value is string text && bool.TryParse(text, out var parsed) && parsed);
    public static T EnumValue<T>(Dictionary<string, object?> map, string key, T fallback) where T : struct, Enum
    {
        if (!map.ContainsKey(key)) return fallback;
        if (Text(map, key) is { } text && Enum.TryParse<T>(text.Replace("_", "").Replace("-", ""), true, out var value) && Enum.IsDefined(value)) return value;
        throw new ArgumentException($"Invalid editor configuration value for '{key}'.");
    }
}

public static class EditorActionCapabilities
{
    public static EditorCapability For(EditorAction action) => action switch
    {
        EditorAction.OpenWorkspace => EditorCapability.OpenWorkspace,
        EditorAction.OpenFile => EditorCapability.OpenFile,
        EditorAction.OpenAtPosition => EditorCapability.OpenAtPosition,
        EditorAction.OpenDiff => EditorCapability.Diff,
        EditorAction.Reveal => EditorCapability.Reveal,
        EditorAction.Focus => EditorCapability.Focus,
        EditorAction.OpenTerminal => EditorCapability.Terminal,
        EditorAction.Restore => EditorCapability.Restore,
        _ => EditorCapability.None,
    };
}
