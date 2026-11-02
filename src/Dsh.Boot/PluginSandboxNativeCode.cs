namespace Dsh.Boot;

/** Enumerates bundled native libraries used by the runtime code closure. */
internal static class PluginSandboxNativeCode
{

    internal static void Add(string artifact, HashSet<string> paths)
    {
        var full = Path.GetFullPath(artifact);
        paths.Add(full);
        foreach (var sibling in Directory.EnumerateFiles(Path.GetDirectoryName(full)!))
        {
            if (PluginSandboxLaunch.PathComparer.Equals(full, sibling) || !IsLibrary(sibling))
                continue;
            paths.Add(Path.GetFullPath(sibling));
        }
    }

    internal static void AddImport(string source, string name, string? resolved, HashSet<string> paths)
    {
        var directory = Path.GetDirectoryName(source)!;
        if (resolved is null)
        {
            var direct = Path.GetFullPath(name, directory);
            if (File.Exists(direct))
                resolved = direct;
            else if (!Path.IsPathRooted(name))
            {
                var file = OperatingSystem.IsWindows() ? name + ".dll"
                    : OperatingSystem.IsMacOS() ? "lib" + name + ".dylib" : "lib" + name + ".so";
                var candidate = Path.GetFullPath(file, directory);
                if (File.Exists(candidate))
                    resolved = candidate;
            }
        }
        if (resolved is not null)
        {
            resolved = Path.GetFullPath(resolved);
            var relative = Path.GetRelativePath(directory, resolved);
            if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
                Add(resolved, paths);
            else
                paths.Add(resolved);
        }
    }

    private static bool IsLibrary(string path)
        => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Contains(".so.", StringComparison.Ordinal)
            || path.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase);

}
