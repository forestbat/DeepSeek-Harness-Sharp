namespace Dsh.RemoteHost;

/**
 * Which paths remote clients may touch through host.file.*, host.fs.* and host.git.*.
 * Allowed roots are the fixed roots (the user's home and the harness home) plus the project path of every registered session:
 * starting a session is the only way to widen the reach, and a session already runs commands in its project anyway.
 * Paths are expanded (`~`), made absolute and resolved through symbolic links before the check, so links cannot point outside.
 */
public sealed class HostPathPolicy(IReadOnlyList<string> fixedRoots, Func<IEnumerable<string>> sessionRoots)
{
    /** Upper bound on link hops while resolving one path, as ELOOP does for the kernel. */
    private const int MaxLinkHops = 40;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /** Fixed roots are the user's home directory plus ExtraRoots; session roots are the project paths in Sessions. */
    public static HostPathPolicy ForHome(HostSessionRegistry sessions, params string[] extraRoots)
        => new([Home, .. extraRoots], () => sessions.List().Select(session => session.ProjectPath).OfType<string>());

    /** `~` and blank map to the home directory, `~/x` below it; anything else is made absolute (relative to the host's working directory). */
    public static string Expand(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0 || trimmed == "~")
            return Home;
        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Home, trimmed[2..]);
        return Path.GetFullPath(trimmed);
    }

    /** The resolved absolute path; throws UnauthorizedAccessException when it lies outside every allowed root. */
    public string Resolve(string path)
    {
        var resolved = RealPath(Expand(path));
        if (!IsAllowed(resolved))
            throw new UnauthorizedAccessException($"path is outside the host's allowed roots: {path}");
        return resolved;
    }

    /**
     * Like Resolve, but a symbolic link in the last component stays unresolved: renaming, deleting or describing a link acts on the link itself.
     * The link's own location is what has to be allowed, not its target.
     */
    public string ResolveEntry(string path)
    {
        var expanded = Expand(path);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(expanded));
        var resolved = parent is null ? expanded : Path.Combine(RealPath(parent), Path.GetFileName(Path.TrimEndingDirectorySeparator(expanded)));
        if (!IsAllowed(resolved))
            throw new UnauthorizedAccessException($"path is outside the host's allowed roots: {path}");
        return resolved;
    }

    /** Whether an already resolved absolute path lies inside an allowed root. */
    public bool IsAllowed(string resolvedPath)
        => fixedRoots.Concat(sessionRoots()).Any(root => IsUnder(resolvedPath, RealPath(Path.GetFullPath(root))));

    private static bool IsUnder(string path, string root)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        if (string.Equals(Path.TrimEndingDirectorySeparator(path), trimmedRoot, PathComparison))
            return true;
        return path.StartsWith($"{trimmedRoot}{Path.DirectorySeparatorChar}", PathComparison)
            || (Path.GetPathRoot(trimmedRoot) == trimmedRoot && path.StartsWith(trimmedRoot, PathComparison));
    }

    /** Resolves symbolic links in every existing component of an absolute path; missing trailing components are kept as written. */
    private static string RealPath(string fullPath, int hops = 0)
    {
        var root = Path.GetPathRoot(fullPath) ?? "";
        var current = root;
        foreach (var part in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            var entry = new FileInfo(next);
            if (entry.LinkTarget is not null && hops < MaxLinkHops && entry.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                next = RealPath(target.FullName, hops + 1);
            current = next;
        }
        return current;
    }
}
