using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dsh.Core;
using YamlDotNet.RepresentationModel;
using System.IO.Enumeration;

namespace Dsh.Ide.Selection;

public sealed class EditorBindings
{
    private readonly ConcurrentDictionary<(string Root, string Session), EditorBinding> bindings = new();
    private static string Key(string root) => OperatingSystem.IsWindows() ? Path.GetFullPath(root).ToUpperInvariant() : Path.GetFullPath(root);
    public EditorBinding? Get(string root, string? session) => bindings.GetValueOrDefault((Key(root), session ?? "")) ?? bindings.GetValueOrDefault((Key(root), ""));
    public void Set(EditorBinding binding) => bindings[(Key(binding.WorkspaceRoot), binding.SessionId ?? "")] = binding;
}

public static class EditorSelection
{
    private static readonly ConcurrentDictionary<string, Regex> Globs = new(StringComparer.Ordinal);
    public static EditorWorkspace InspectWorkspace(string root)
    {
        root = Path.GetFullPath(root);
        var markers = Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray() : [];
        var configPath = Path.Combine(root, ".dsh", "ide.yaml");
        if (!File.Exists(configPath)) return new(root, markers);
        if (!EditorPaths.IsWithin(configPath, root)) throw new ArgumentException("Workspace preference file resolves outside the workspace.");
        using var reader = File.OpenText(configPath);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0) return new(root, markers);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode map) throw new ArgumentException("Workspace IDE preferences must be one mapping.");
        RejectExecutableConfiguration(map);
        string? Value(string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? (node as YamlScalarNode)?.Value : null;
        var preferred = Value("default_editor");
        if (preferred == "auto") preferred = null;
        var triggerOrder = Array.Empty<string>();
        bool? enabled = null;
        if (map.Children.TryGetValue(new YamlScalarNode("triggers"), out var triggers))
        {
            if (triggers is YamlScalarNode scalar && bool.TryParse(scalar.Value, out var parsed)) enabled = parsed;
            else if (triggers is YamlSequenceNode sequence) triggerOrder = sequence.Children.OfType<YamlScalarNode>().Select(n => n.Value ?? "").ToArray();
            else if (triggers is YamlMappingNode triggerMap)
            {
                if (triggerMap.Children.TryGetValue(new YamlScalarNode("enabled"), out var flag) && bool.TryParse((flag as YamlScalarNode)?.Value, out var parsedFlag)) enabled = parsedFlag;
                if (triggerMap.Children.TryGetValue(new YamlScalarNode("order"), out var order) && order is YamlSequenceNode sequenceOrder) triggerOrder = sequenceOrder.Children.OfType<YamlScalarNode>().Select(n => n.Value ?? "").ToArray();
            }
        }
        return new(root, markers) { PreferredEditor = preferred, ProjectPath = Value("project") ?? Value("solution") ?? Value("project_path"), TriggerOrder = triggerOrder, TriggersEnabled = enabled };
    }
    private static void RejectExecutableConfiguration(YamlNode node)
    {
        var visited = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<YamlNode>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is YamlMappingNode map)
                foreach (var (key, value) in map.Children)
                {
                    if (key is YamlScalarNode scalar && scalar.Value is "command" or "args" or "actions" or "env" or "cwd")
                        throw new ArgumentException("Workspace preferences may reference editor IDs only; process configuration must be global.");
                    pending.Push(value);
                }
            else if (current is YamlSequenceNode sequence)
                foreach (var item in sequence.Children) pending.Push(item);
        }
    }
    public static bool Matches(EditorDescriptor descriptor, EditorRequest request, EditorWorkspace workspace) => Matches(descriptor.When, workspace, request);
    public static bool Matches(EditorConditions conditions, EditorWorkspace workspace, EditorRequest request) => request.Action switch
    {
        EditorAction.OpenWorkspace => MatchesWorkspace(conditions, workspace),
        EditorAction.OpenFile or EditorAction.OpenAtPosition or EditorAction.OpenDiff or EditorAction.Reveal => MatchesFile(conditions, workspace, request.Path ?? request.Right ?? request.Left),
        _ => true,
    };
    public static bool MatchesAll(EditorConditions conditions, EditorWorkspace workspace, string? path)
        => MatchesWorkspace(conditions, workspace)
           && (conditions.Extensions.Length == 0 || !string.IsNullOrEmpty(path) && MatchesExtensions(conditions.Extensions, path))
           && (conditions.Globs.Length == 0 || !string.IsNullOrEmpty(path) && MatchesGlobs(conditions.Globs, workspace, path));
    private static bool MatchesWorkspace(EditorConditions conditions, EditorWorkspace workspace)
    {
        if (conditions.Workspace.Length == 0) return true;
        foreach (var marker in workspace.Markers)
            if (MatchesWorkspaceMarker(conditions, marker)) return true;
        return false;
    }
    internal static bool MatchesWorkspaceMarker(EditorConditions conditions, string marker)
    {
        foreach (var pattern in conditions.Workspace)
            if (Glob(pattern, marker)) return true;
        return false;
    }
    private static bool MatchesFile(EditorConditions conditions, EditorWorkspace workspace, string? path)
        => conditions.Extensions.Length == 0 && conditions.Globs.Length == 0
           || !string.IsNullOrEmpty(path) && (MatchesExtensions(conditions.Extensions, path) || MatchesGlobs(conditions.Globs, workspace, path));
    private static bool MatchesExtensions(ReadOnlySpan<string> extensions, string path)
    {
        var extension = Path.GetExtension(path);
        foreach (var ext in extensions)
            if (string.Equals(ext.StartsWith('.') ? ext : $".{ext}", extension, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    private static bool MatchesGlobs(ReadOnlySpan<string> patterns, EditorWorkspace workspace, string path)
    {
        if (patterns.IsEmpty) return false;
        var relative = Path.GetRelativePath(workspace.Root, Path.GetFullPath(path, workspace.Root)).Replace('\\', '/');
        var file = Path.GetFileName(path);
        foreach (var pattern in patterns)
            if (Glob(pattern, relative) || Glob(pattern, file)) return true;
        return false;
    }
    public static bool Glob(string pattern, string value) => Globs.GetOrAdd(pattern, static key =>
    {
        var escaped = Regex.Escape(key.Replace('\\', '/')).Replace("\\*\\*/", "(?:.*/)?").Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]");
        return new Regex($"\\A{escaped}\\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    }).IsMatch(value.Replace('\\', '/'));
    public static string? DetectHostEditor(IReadOnlyList<EditorHostRule> rules)
    {
        foreach (var rule in rules)
        {
            if (Environment.GetEnvironmentVariable(rule.Variable) is not { } value
                || !FileSystemName.MatchesSimpleExpression(rule.Pattern, value, ignoreCase: true)) continue;
            return rule.Editor.Value;
        }
        return null;
    }
}

public static class EditorPaths
{
    public static string PhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null) current = entry.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Cannot resolve symbolic link.");
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    public static bool IsWithin(string path, string workspace)
    {
        var root = PhysicalPath(workspace);
        var target = PhysicalPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : $"{root}{Path.DirectorySeparatorChar}";
        return target.Equals(root, comparison) || target.StartsWith(prefix, comparison);
    }
    public static EditorRequest Normalize(EditorRequest request, bool allowOutside, IReadOnlyList<string>? trustedSnapshotRoots = null)
    {
        var root = Path.GetFullPath(request.WorkspaceRoot);
        string? NormalizePath(string? path) => path is null ? null : Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile ? Path.GetFullPath(uri.LocalPath) : Path.GetFullPath(path, root);
        var result = request with { WorkspaceRoot = root, Path = NormalizePath(request.Path), Left = NormalizePath(request.Left), Right = NormalizePath(request.Right) };
        foreach (var path in new[] { result.Path, result.Left, result.Right }.OfType<string>())
        {
            if (path.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("Editor paths cannot contain control characters.");
            var trustedSnapshot = result.Action == EditorAction.OpenDiff && (path == result.Left || path == result.Right) && path != result.Path
                && trustedSnapshotRoots is not null && trustedSnapshotRoots.Any(trusted => IsWithin(path, trusted));
            if (!allowOutside && !IsWithin(path, root) && !trustedSnapshot) throw new EditorOutsideWorkspaceException();
        }
        if (result.Line < 1 || result.Column < 1) throw new ArgumentException("Editor line and column must be positive.");
        if (result.Action == EditorAction.OpenDiff && (result.Left is null || result.Right is null)) throw new ArgumentException("Diff requires left and right paths.");
        if (result.Action == EditorAction.OpenDiff && (!File.Exists(result.Left) || !File.Exists(result.Right))) throw new FileNotFoundException("Editor diff requires two existing files.");
        return result;
    }
}
public sealed class EditorOutsideWorkspaceException : IOException
{
    public EditorOutsideWorkspaceException() : base("Editor target resolves outside the workspace.") { }
}
