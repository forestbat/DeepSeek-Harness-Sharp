namespace Dsh.Runtime;

/// <summary>Canonical filesystem path identity, including symbolic links and Windows namespace validation.</summary>
public static class CanonicalFilePath
{
    public static bool SameFile(string left, string right)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return comparer.Equals(Canonicalize(left), Canonicalize(right));
    }

    public static string Canonicalize(string path, bool allowNetwork = true)
    {
        var full = Path.GetFullPath(NormalizeNamespace(path));
        for (var links = 0; links <= 40; links++)
        {
            DemandFilePath(full);
            if (!allowNetwork && IsNetworkPath(full))
                throw new UnauthorizedAccessException("Network file paths are not permitted.");
            var resolved = ResolveFirstLink(full);
            if (resolved is null)
                return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
            full = NormalizeNamespace(resolved);
        }
        throw new IOException("The file path exceeds the symbolic-link resolution limit.");
    }

    private static string? ResolveFirstLink(string full)
    {
        var root = Path.GetPathRoot(full)!;
        var current = root;
        var relative = full.AsSpan(root.Length);
        foreach (var range in relative.Split(Path.DirectorySeparatorChar))
        {
            if (range.Start.Value == range.End.Value)
                continue;
            current = Path.Join(current.AsSpan(), relative[range]);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not { } link)
                continue;
            var target = Path.GetFullPath(link, Path.GetDirectoryName(current)!);
            return Path.Join(target.AsSpan(), relative[range.End.GetOffset(relative.Length)..]);
        }
        return null;
    }

    public static void DemandFilePath(string path)
    {
        if (OperatingSystem.IsWindows())
            RejectDeviceNamespace(path);
    }

    public static bool IsNetworkPath(string path)
        => OperatingSystem.IsWindows() && (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal));

    internal static string NormalizeNamespace(string path)
    {
        const string extendedPrefix = @"\\?\", uncPrefix = @"\\?\UNC\";
        if (!OperatingSystem.IsWindows() || path.Length < extendedPrefix.Length
            || path[0] is not ('\\' or '/') || path[1] is not ('\\' or '/')
            || path[2] != '?' || path[3] is not ('\\' or '/'))
            return path;
        if (path.StartsWith("//?/", StringComparison.Ordinal))
            path = path.Replace('/', '\\');
        else if (path.Contains('/'))
            throw new NotSupportedException("Extended-length file paths require consistent separators.");
        var suffix = path.AsSpan(extendedPrefix.Length);
        foreach (var range in suffix.Split('\\'))
        {
            var component = suffix[range];
            if (!component.IsEmpty && component[^1] is ' ' or '.')
                throw new NotSupportedException("Extended-length file paths with trailing spaces or dots cannot be normalized.");
        }
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return $@"\\{path.AsSpan(uncPrefix.Length)}";
        if (suffix.Length >= 3 && char.IsAsciiLetter(suffix[0]) && suffix[1] == ':' && suffix[2] == '\\')
            return path[extendedPrefix.Length..];
        throw new NotSupportedException("Unsupported Windows filesystem namespace.");
    }

    private static void RejectDeviceNamespace(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.AsSpan(root.Length).Contains(':'))
            throw new NotSupportedException("Plugin file capabilities do not expose device namespaces or alternate data streams.");
        var relative = path.AsSpan(root.Length);
        foreach (var range in relative.Split(Path.DirectorySeparatorChar))
        {
            var component = relative[range];
            var extension = component.IndexOf('.');
            var name = (extension >= 0 ? component[..extension] : component).TrimEnd();
            if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || name.Length == 4 && (name[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
                    || name[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9')
                throw new NotSupportedException("Plugin file capabilities do not expose DOS device names.");
        }
    }
}
