using Dsh.Runtime;

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
        MigrateInBackground(initialHome, target, settings.Storage);
        return target;
    }

    /** §18 统一路径集(根 + 可配置分区): 各模块从这里取路径, 不再自己拼 `root/xxx`。 */
    public static DshPaths Paths(HarnessHome home, HarnessSettings settings)
        => DshPaths.Resolve(home.Root, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DshPaths.Sessions] = settings.Storage?.Sessions,
            [DshPaths.Logs] = settings.Storage?.Logs,
            [DshPaths.Cache] = settings.Storage?.Cache,
            [DshPaths.Checkpoints] = settings.Storage?.Checkpoints,
            [DshPaths.Attachments] = settings.Storage?.Attachments,
            [DshPaths.Telemetry] = settings.Storage?.Telemetry,
            [DshPaths.Run] = settings.Storage?.Run,
        });

    /**
     * 字形缓存等没有 ctx 的调用点用的 cache 分区: 按默认根(含 storage.root 记录)读 settings 的 storage.cache,
     * 用不了 settings 时退回默认根的 cache/。每次调用都读, 不缓存(测试会换根)。
     */
    public static string ResolveCachePath()
    {
        var home = ResolveDefaultHome();
        try
        {
            return Paths(home, HarnessSettings.Load(home)).CachePath;
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            return Path.Combine(home.Root, DshPaths.Cache);
        }
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
        try
        {
            HarnessSettings.CopyIfMissing(from, to);
        }
        catch (IOException)
        {
        }
    }

    private static void MigrateInBackground(HarnessHome from, HarnessHome to, StorageSettings? settings)
    {
        _ = Task.Run(() => Migrate(from, to, settings, progress: null));
    }

    /** 迁移日志: 新旧根都可能被搬走, 所以写在"目标根"下, 便于事后核对。 */
    public const string MigrateLogName = ".storage-migrate.log";

    /** 跨进程迁移锁: 同一时刻只允许一个进程搬同一个目标根(daemon/ACP/TUI 共用 root)。 */
    public const string MigrateLockName = ".storage-migrate.lock";

    /**
     * 把旧根的数据分区搬到新根(D14): 先独占锁 → 逐个分区 move(不行就 copy+删) → 目标已存在同名非空目录即跳过
     * (可重入, 下次启动从未完成处继续)。搬空后旧根(若非默认根)改名为 `<old>.migrated-<ts>` 归档, 不直接删除。
     * 失败只记日志、不抛: 启动不能被迁移拖住。
     */
    public static IReadOnlyList<(string Directory, string Outcome)> Migrate(
        HarnessHome from,
        HarnessHome to,
        StorageSettings? settings = null,
        Action<string>? progress = null)
    {
        var results = new List<(string, string)>();
        if (SamePath(from.Root, to.Root))
            return results;

        using var migrationLock = TryAcquireMigrateLock(to);
        if (migrationLock is null)
        {
            Log(to, $"skip: another process is migrating into {to.Root}");
            progress?.Invoke("skip: another process is migrating");
            return results;
        }

        Log(to, $"start: {from.Root} -> {to.Root}");
        progress?.Invoke($"migrating {from.Root} -> {to.Root}");
        foreach (var directory in MigrateDirectories(from, settings))
        {
            var outcome = MigrateDirectory(from, to, directory);
            results.Add((directory, outcome));
            if (outcome != "absent")
            {
                Log(to, $"{directory}: {outcome}");
                progress?.Invoke($"{directory}: {outcome}");
            }
        }

        ArchiveOldRoot(from, to, results, progress);
        Log(to, "done");
        return results;
    }

    private static FileStream? TryAcquireMigrateLock(HarnessHome to)
    {
        try
        {
            Directory.CreateDirectory(to.Root);
            return new FileStream(
                Path.Combine(to.Root, MigrateLockName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /** 分区名 → 旧根下的实际目录: 默认名 ∪ settings 里配置的分区值(相对旧根的)。 */
    private static IReadOnlyList<string> MigrateDirectories(HarnessHome from, StorageSettings? settings)
    {
        var names = new List<string>(from.DataDirectories);
        var configured = DshPaths.Resolve(from.Root, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DshPaths.Sessions] = settings?.Sessions,
            [DshPaths.Logs] = settings?.Logs,
            [DshPaths.Cache] = settings?.Cache,
            [DshPaths.Checkpoints] = settings?.Checkpoints,
            [DshPaths.Attachments] = settings?.Attachments,
            [DshPaths.Telemetry] = settings?.Telemetry,
            [DshPaths.Run] = settings?.Run,
        });
        foreach (var name in DshPaths.Names)
        {
            var relative = Path.GetRelativePath(from.Root, configured.Path(name));
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                continue;
            if (!names.Contains(relative, StringComparer.OrdinalIgnoreCase))
                names.Add(relative);
        }
        return names;
    }

    private static string MigrateDirectory(HarnessHome from, HarnessHome to, string directory)
    {
        var source = Path.Combine(from.Root, directory);
        if (!File.Exists(source) && !Directory.Exists(source))
            return "absent";
        var destination = Path.Combine(to.Root, directory);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            return "skipped (destination exists)";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: false);
            Directory.Move(source, destination);
            return "moved";
        }
        catch (IOException)
        {
            return TryCopyThenDelete(source, destination);
        }
        catch (UnauthorizedAccessException)
        {
            return TryCopyThenDelete(source, destination);
        }
    }

    /** 旧根搬空后归档(默认根不归档: DSH_HOME/记录 marker 还要留在这里)。 */
    private static void ArchiveOldRoot(
        HarnessHome from,
        HarnessHome to,
        IReadOnlyList<(string Directory, string Outcome)> results,
        Action<string>? progress)
    {
        if (!results.Any(result => result.Outcome == "moved"))
            return;
        if (SamePath(from.Root, to.Root))
            return;
        try
        {
            var defaultRoot = HarnessHome.Default().Root;
            var archive = $"{Path.TrimEndingDirectorySeparator(from.Root)}.migrated-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            var source = Path.TrimEndingDirectorySeparator(from.Root);
            if (SamePath(from.Root, defaultRoot))
            {
                // 默认根保留: 里面还有 marker 与 settings.yaml, 数据分区已搬走。
                Log(to, $"kept old root {source} (default root holds the storage-root marker)");
                return;
            }
            Directory.Move(source, archive);
            Log(to, $"archived old root -> {archive}");
            progress?.Invoke($"old root archived: {archive}");
        }
        catch (IOException error)
        {
            Log(to, $"archive failed: {error.Message}");
        }
        catch (UnauthorizedAccessException error)
        {
            Log(to, $"archive failed: {error.Message}");
        }
    }

    private static void Log(HarnessHome to, string message)
    {
        try
        {
            Directory.CreateDirectory(to.Root);
            File.AppendAllText(
                Path.Combine(to.Root, MigrateLogName),
                $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string TryCopyThenDelete(string source, string destination)
    {
        try
        {
            CopyTree(source, destination);
            if (Directory.Exists(source))
                Directory.Delete(source, recursive: true);
            else
                File.Delete(source);
            return "copied";
        }
        catch (IOException error)
        {
            return $"failed: {error.Message}";
        }
        catch (UnauthorizedAccessException error)
        {
            return $"failed: {error.Message}";
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
