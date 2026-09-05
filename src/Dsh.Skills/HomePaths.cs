namespace Dsh.Skills;

public static class HomePaths
{
    public const string DshHomeDirName = ".dsh";
    public const string DshHomeEnv = "DSH_HOME";

    public static string DefaultDshHome()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DshHomeDirName);

    public static string ExpandHomePath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~")
            return home;
        if (path.StartsWith("~/") || path.StartsWith("~\\"))
            return Path.Combine(home, path[2..]);
        return path;
    }

    public static string ResolveDshHome(string? configured)
    {
        var fromEnv = Environment.GetEnvironmentVariable(DshHomeEnv);
        var selected = configured ?? (!string.IsNullOrWhiteSpace(fromEnv) ? fromEnv : DefaultDshHome());
        return Path.GetFullPath(ExpandHomePath(selected));
    }

    public static string CanonicalizeWatchPath(string path)
    {
        var current = Path.GetFullPath(path);
        var missing = new List<string>();
        while (true)
        {
            var resolved = TryRealPath(current);
            if (resolved is not null)
            {
                if (missing.Count > 0 && !Directory.Exists(resolved))
                    throw new IOException($"watch path ancestor \"{resolved}\" is not a directory");
                missing.Reverse();
                return missing.Count == 0 ? resolved : Path.Combine([resolved, ..missing]);
            }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
                throw new FileNotFoundException($"no existing ancestor for watch path \"{path}\"");
            missing.Add(Path.GetFileName(current));
            current = parent;
        }
    }

    private static string? TryRealPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
            if (File.Exists(path))
                return new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }
}
