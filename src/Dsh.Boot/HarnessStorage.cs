namespace Dsh.Boot;

/** §18 统一持久化根的启动逻辑: 默认根(marker 记录) → settings.storage.root → 后台迁移旧数据。 */
public static class HarnessStorage
{
    /** 起始根: 默认根若记录了 storage.root, 直接返回记录的根, 否则返回默认根。 */
    public static HarnessHome ResolveDefaultHome()
    {
        var defaultHome = HarnessHome.Default();
        if (ReadRecordedRoot(defaultHome) is { } recorded)
            return recorded;
        return defaultHome;
    }

    /**
     * 若 settings.storage.root 指向另一个根: 立刻切到新根, settings.yaml 缺失则复制, 并在默认根写下记录,
     * 旧根的数据目录后台搬运。新根已有同名目录时不覆盖。
     */
    public static HarnessHome ApplyStorageRoot(HarnessHome initialHome, HarnessSettings settings)
    {
        if (settings.Storage?.Root is not { Length: > 0 } configured)
            return initialHome;
        var target = HarnessHome.Resolve(configured);
        if (SamePath(target.Root, initialHome.Root))
            return initialHome;

        target.Ensure();
        CopySettingsIfMissing(initialHome, target);
        var defaultHome = HarnessHome.Default();
        if (SamePath(defaultHome.Root, initialHome.Root))
            WriteRecordedRoot(defaultHome, target);
        MigrateInBackground(initialHome, target);
        return target;
    }

    private static HarnessHome? ReadRecordedRoot(HarnessHome defaultHome)
    {
        var marker = defaultHome.SubPath(HarnessHome.StorageRootMarker);
        if (!File.Exists(marker))
            return null;
        try
        {
            var recorded = File.ReadAllText(marker).Trim();
            return recorded.Length == 0 ? null : HarnessHome.Resolve(recorded);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void WriteRecordedRoot(HarnessHome defaultHome, HarnessHome target)
    {
        try
        {
            Directory.CreateDirectory(defaultHome.Root);
            File.WriteAllText(defaultHome.SubPath(HarnessHome.StorageRootMarker), target.Root);
        }
        catch (IOException)
        {
        }
    }

    private static void CopySettingsIfMissing(HarnessHome from, HarnessHome to)
    {
        if (File.Exists(to.SettingsFile) || !File.Exists(from.SettingsFile))
            return;
        try
        {
            File.Copy(from.SettingsFile, to.SettingsFile);
            SettingsFilePermissions.Restrict(to.SettingsFile);
        }
        catch (IOException)
        {
        }
    }

    private static void MigrateInBackground(HarnessHome from, HarnessHome to)
    {
        _ = Task.Run(() =>
        {
            foreach (var directory in from.DataDirectories)
            {
                var source = Path.Combine(from.Root, directory);
                if (!Directory.Exists(source))
                    continue;
                var destination = Path.Combine(to.Root, directory);
                if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                    continue;
                try
                {
                    Directory.CreateDirectory(to.Root);
                    if (Directory.Exists(destination))
                        Directory.Delete(destination, recursive: false);
                    Directory.Move(source, destination);
                }
                catch (IOException)
                {
                    TryCopyThenDelete(source, destination);
                }
                catch (UnauthorizedAccessException)
                {
                    TryCopyThenDelete(source, destination);
                }
            }
        });
    }

    private static void TryCopyThenDelete(string source, string destination)
    {
        try
        {
            CopyTree(source, destination);
            Directory.Delete(source, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static bool SamePath(string left, string right)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
