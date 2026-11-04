using Dsh.Core;
using Dsh.Ide.Selection;

namespace Dsh.Ide.Bridge;

public enum EditorPathMappingKind { LocalMirror, Ssh, Wsl, DevContainer }

/// <summary>Local user configuration only. Container authorities are copied from an existing trusted Remote Containers workspace.</summary>
public sealed record EditorPathMapping(
    [property: Dsh.Plugins.DshPluginSettings(Name = "machine")] string Machine,
    [property: Dsh.Plugins.DshPluginSettings(Name = "source_root")] string SourceRoot,
    [property: Dsh.Plugins.DshPluginSettings(Name = "destination_root")] string DestinationRoot,
    EditorPathMappingKind Kind = EditorPathMappingKind.LocalMirror, string? Authority = null, string? Editor = null);

public sealed class EditorPathMapper
{
    private readonly EditorPathMapping[] _mappings;
    public IReadOnlyList<EditorPathMapping> Mappings => _mappings;
    public static EditorPathMapper From(object? globalPluginConfiguration)
    {
        var config = ConfigMap.Read(globalPluginConfiguration);
        var mappings = ConfigMap.Items(config.GetValueOrDefault("client_mappings")).Select(value =>
        {
            var map = ConfigMap.Read(value);
            if (map.Keys.Any(key => key is not ("machine" or "source_root" or "destination_root" or "kind" or "authority" or "editor")))
                throw new ArgumentException("Client mappings accept a machine, paths, a mapping kind, an authority, and an editor ID only.");
            return new EditorPathMapping(
                ConfigMap.Text(map, "machine") ?? throw new ArgumentException("Client mapping requires machine."),
                ConfigMap.Text(map, "source_root") ?? throw new ArgumentException("Client mapping requires source_root."),
                ConfigMap.Text(map, "destination_root") ?? throw new ArgumentException("Client mapping requires destination_root."),
                ConfigMap.EnumValue(map, "kind", EditorPathMappingKind.LocalMirror),
                ConfigMap.Text(map, "authority"), ConfigMap.Text(map, "editor"));
        });
        return new EditorPathMapper(mappings);
    }
    public EditorPathMapper(IEnumerable<EditorPathMapping> mappings)
    {
        _mappings = mappings.Select(mapping => mapping with
        {
            SourceRoot = CanonicalRemote(mapping.SourceRoot),
            DestinationRoot = Path.GetFullPath(mapping.DestinationRoot),
        }).OrderByDescending(mapping => mapping.SourceRoot.Length).ToArray();
        foreach (var mapping in _mappings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mapping.Machine);
            if (!Directory.Exists(mapping.DestinationRoot))
                throw new ArgumentException("A remote IDE mapping requires an existing local destination directory.");
            if (mapping.Kind != EditorPathMappingKind.LocalMirror) _ = RemoteAuthority(mapping);
        }
    }

    public EditorRequest Map(string machine, EditorRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machine);
        // Incoming URIs cannot override a locally selected host, distro, container, or editor.
        if (request.RemoteUri is not null) throw new ArgumentException("Remote requests cannot supply URI mappings.");
        var workspace = CanonicalRemote(request.WorkspaceRoot);
        var mapping = _mappings.FirstOrDefault(value => value.Machine.Equals(machine, StringComparison.Ordinal) && IsUnder(workspace, value.SourceRoot))
            ?? throw new ArgumentException("No trusted mapping exists for this remote workspace.");
        string? RemotePath(string? path) => path is null ? null : CanonicalRemote(IsAbsolute(path) ? path : workspace + "/" + path);
        string LocalPath(string path)
        {
            if (!IsUnder(path, workspace) || !IsUnder(path, mapping.SourceRoot))
                throw new ArgumentException("The remote target escapes its mapped workspace.");
            var suffix = path[mapping.SourceRoot.Length..].TrimStart('/');
            var local = Path.GetFullPath(Path.Combine(mapping.DestinationRoot, suffix.Replace('/', Path.DirectorySeparatorChar)));
            if (!EditorPaths.IsWithin(local, mapping.DestinationRoot))
                throw new ArgumentException("The local mapping resolves outside its destination.");
            return local;
        }
        var path = RemotePath(request.Path);
        var left = RemotePath(request.Left);
        var right = RemotePath(request.Right);
        var localRoot = LocalPath(workspace);
        var mapped = request with
        {
            WorkspaceRoot = localRoot,
            Path = path is null ? null : LocalPath(path),
            Left = left is null ? null : LocalPath(left),
            Right = right is null ? null : LocalPath(right),
            RemoteUri = null,
            ExecutionTarget = EditorExecutionTarget.Local,
            HostEditor = null,
            MachineId = null,
        };
        if (mapping.Kind == EditorPathMappingKind.LocalMirror) return mapped;
        if (request.Action is not (EditorAction.OpenWorkspace or EditorAction.OpenFile or EditorAction.OpenAtPosition))
            throw new NotSupportedException("This remote URI profile supports workspace and file opening only.");
        var target = path ?? workspace;
        return mapped with { Editor = mapping.Editor, RemoteUri = BuildUri(mapping, target), TriggerEditors = [], ExcludedEditors = [] };
    }

    public bool HasMapping(string machine, string workspace)
    {
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(workspace)) return false;
        string canonical;
        try { canonical = CanonicalRemote(workspace); }
        catch (ArgumentException) { return false; }
        return _mappings.Any(value => value.Machine.Equals(machine, StringComparison.Ordinal) && IsUnder(canonical, value.SourceRoot));
    }

    public static string BuildUri(EditorPathMapping mapping, string path)
    {
        var root = CanonicalRemote(mapping.SourceRoot);
        var target = CanonicalRemote(path);
        if (!IsUnder(target, root)) throw new ArgumentException("The URI path is outside the mapped remote root.");
        var encoded = string.Join('/', target.Split('/').Select(Uri.EscapeDataString));
        if (!encoded.StartsWith('/')) encoded = "/" + encoded;
        return "vscode-remote://" + RemoteAuthority(mapping) + encoded;
    }

    private static string RemoteAuthority(EditorPathMapping mapping)
    {
        var value = mapping.Authority;
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or '+')))
            throw new ArgumentException("Remote mappings require an explicit safe host alias, WSL distribution, or container authority.");
        return mapping.Kind switch
        {
            EditorPathMappingKind.Ssh => "ssh-remote+" + value,
            EditorPathMappingKind.Wsl => "wsl+" + value,
            EditorPathMappingKind.DevContainer when value.StartsWith("dev-container+", StringComparison.Ordinal) || value.StartsWith("attached-container+", StringComparison.Ordinal) => value,
            _ => throw new ArgumentException("A container mapping requires its full Remote Containers authority."),
        };
    }

    private static bool IsAbsolute(string path)
        => path.StartsWith('/') || path.StartsWith('\\') || path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\';
    private static bool IsUnder(string path, string root)
    {
        var comparison = root.Length > 1 && root[1] == ':' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root.TrimEnd('/') + "/", comparison);
    }
    private static string CanonicalRemote(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !IsAbsolute(path))
            throw new ArgumentException("Remote paths must be absolute and contain no control characters.");
        path = path.Replace('\\', '/');
        if (path.StartsWith("//", StringComparison.Ordinal)) throw new ArgumentException("UNC mappings are not implicit remote authorities.");
        var prefix = path.StartsWith('/') ? "/" : path[..3];
        var parts = new List<string>();
        foreach (var component in path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            if (component == "..")
            {
                if (parts.Count == 0) throw new ArgumentException("The remote path escapes its filesystem root.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(component);
        }
        return prefix + string.Join('/', parts);
    }
}
