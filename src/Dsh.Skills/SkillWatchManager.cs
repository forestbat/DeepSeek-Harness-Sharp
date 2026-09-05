using Cordis;

namespace Dsh.Skills;

internal sealed class SkillWatchManager
{
    public const int DefaultStabilityThresholdMs = 200;
    public const int DefaultPollIntervalMs = 100;
    public const int DefaultMaxProjects = 128;

    internal sealed record ResolvedWatchConfig(
        bool Enabled,
        bool UsePolling,
        int StabilityThresholdMs,
        int PollIntervalMs,
        int MaxProjects,
        bool FollowSymlinks);

    internal abstract record RootWatchMode
    {
        public sealed record Root(string Anchor) : RootWatchMode;

        public sealed record Ancestor(string Anchor, string NextPath) : RootWatchMode;
    }

    private sealed class RootWatchState(SkillRoot root)
    {
        public SkillRoot Root { get; } = root;
        public HashSet<string> Owners { get; } = [];
        public WatchHandle? Watcher { get; set; }
        public bool Unhealthy { get; set; } = true;
    }

    private abstract class WatchHandle(RootWatchMode mode)
    {
        public RootWatchMode Mode { get; } = mode;

        public abstract void Close();
    }

    private sealed class RootWatchHandle(RootWatchMode.Root mode, FileSystemWatcher watcher) : WatchHandle(mode)
    {
        public override void Close()
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private sealed class AncestorWatchHandle : WatchHandle
    {
        private readonly Timer _timer;
        private (bool Exists, long WriteTicks)? _last;
        private int _ticking;

        public AncestorWatchHandle(RootWatchMode.Ancestor mode, int pollIntervalMs, Action onChanged) : base(mode)
        {
            _timer = new Timer(_ => Tick((RootWatchMode.Ancestor)Mode, onChanged), null, pollIntervalMs, pollIntervalMs);
        }

        private void Tick(RootWatchMode.Ancestor mode, Action onChanged)
        {
            if (Interlocked.Exchange(ref _ticking, 1) != 0)
                return;
            try
            {
                var probe = Probe(mode.NextPath);
                var last = _last;
                _last = probe;
                if (last is not null && last.Value != probe)
                    onChanged();
            }
            finally
            {
                Interlocked.Exchange(ref _ticking, 0);
            }
        }

        private static (bool Exists, long WriteTicks) Probe(string path)
        {
            if (Directory.Exists(path))
                return (true, Directory.GetLastWriteTimeUtc(path).Ticks);
            if (File.Exists(path))
                return (true, File.GetLastWriteTimeUtc(path).Ticks);
            return (false, 0);
        }

        public override void Close() => _timer.Dispose();
    }

    internal static class WatchEvents
    {
        public const string Add = "add";
        public const string AddDir = "addDir";
        public const string Change = "change";
        public const string Unlink = "unlink";
        public const string UnlinkDir = "unlinkDir";
    }

    private readonly Context _ctx;
    private readonly Action _invalidate;
    private readonly ResolvedWatchConfig _config;
    private readonly Dictionary<string, RootWatchState> _roots = [];
    private readonly Dictionary<string, HashSet<string>> _projects = [];
    private readonly List<string> _projectOrder = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _debounceSync = new();
    private Timer? _debounceTimer;
    private volatile bool _closing;
    private int _invalidationQueued;

    public SkillWatchManager(Context ctx, Action invalidate, ResolvedWatchConfig config)
    {
        _ctx = ctx;
        _invalidate = invalidate;
        _config = config;
    }

    public static ResolvedWatchConfig ResolveConfig(SkillFilesystemConfig config)
    {
        AssertPositiveInteger(nameof(config.WatchStabilityThresholdMs), config.WatchStabilityThresholdMs);
        AssertPositiveInteger(nameof(config.WatchPollIntervalMs), config.WatchPollIntervalMs);
        AssertPositiveInteger(nameof(config.WatchMaxProjects), config.WatchMaxProjects);
        return new ResolvedWatchConfig(
            config.Watch,
            config.WatchUsePolling,
            config.WatchStabilityThresholdMs,
            config.WatchPollIntervalMs,
            config.WatchMaxProjects,
            config.WatchFollowSymlinks);
    }

    private static void AssertPositiveInteger(string field, int value)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(field, value, $"skill-filesystem: {field} must be a positive integer");
    }

    public async Task ObserveRoots(IReadOnlyList<SkillRoot> roots)
    {
        if (_closing)
            return;
        var evictedProject = false;
        await _gate.WaitAsync();
        try
        {
            var projectRoots = new Dictionary<string, List<SkillRoot>>();
            foreach (var root in roots)
            {
                if (root.ProjectRoot is null)
                {
                    RetainRoot(root, $"shared:{root.Path}");
                    continue;
                }
                if (!projectRoots.TryGetValue(root.ProjectRoot, out var grouped))
                    projectRoots[root.ProjectRoot] = grouped = [];
                grouped.Add(root);
            }
            foreach (var (projectRoot, grouped) in projectRoots)
            {
                _projects.Remove(projectRoot);
                _projectOrder.Remove(projectRoot);
                _projects[projectRoot] = grouped.Select(root => root.Path).ToHashSet();
                _projectOrder.Add(projectRoot);
                foreach (var root in grouped)
                    RetainRoot(root, $"project:{projectRoot}");
            }
            while (_projects.Count > _config.MaxProjects)
            {
                var oldest = _projectOrder[0];
                _projectOrder.RemoveAt(0);
                var paths = _projects[oldest];
                _projects.Remove(oldest);
                foreach (var path in paths)
                    ReleaseRoot(path, $"project:{oldest}");
                evictedProject = true;
            }
        }
        finally
        {
            _gate.Release();
        }
        if (evictedProject)
            _invalidate();
    }

    public void ObserveHostMutation(string path)
    {
        if (_closing)
            return;
        var normalized = Path.GetFullPath(path);
        if (!_roots.Values.Any(state => IsPotentialSkillPath(state.Root, normalized)))
            return;
        _invalidate();
    }

    public async Task Dispose()
    {
        _closing = true;
        await _gate.WaitAsync();
        try
        {
            lock (_debounceSync)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
            var states = _roots.Values.ToList();
            _roots.Clear();
            _projects.Clear();
            _projectOrder.Clear();
            foreach (var state in states)
            {
                var watcher = state.Watcher;
                state.Watcher = null;
                if (watcher is not null)
                    CloseWatcher(watcher);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RetainRoot(SkillRoot root, string owner)
    {
        if (!_roots.TryGetValue(root.Path, out var state))
        {
            state = new RootWatchState(root);
            _roots[root.Path] = state;
        }
        state.Owners.Add(owner);
        if (_config.Enabled)
            EnsureWatcher(state);
    }

    private void ReleaseRoot(string path, string owner)
    {
        if (!_roots.TryGetValue(path, out var state))
            return;
        state.Owners.Remove(owner);
        if (state.Owners.Count > 0)
            return;
        _roots.Remove(path);
        var watcher = state.Watcher;
        state.Watcher = null;
        if (watcher is not null)
            CloseWatcher(watcher);
    }

    private void EnsureWatcher(RootWatchState state)
    {
        if (_closing || !_config.Enabled)
            return;
        var watcher = state.Watcher;
        if (watcher is not null && !state.Unhealthy)
        {
            var current = ResolveRootWatchMode(state.Root.Path, _config.FollowSymlinks);
            if (!state.Unhealthy && SameWatchMode(watcher.Mode, current))
                return;
        }
        ReplaceWatcher(state);
    }

    private void ReplaceWatcher(RootWatchState state)
    {
        var previous = state.Watcher;
        state.Watcher = null;
        if (previous is not null)
            CloseWatcher(previous);
        if (_closing || state.Owners.Count == 0)
            return;
        try
        {
            var watcher = OpenStableWatcher(state);
            if (watcher is null)
                return;
            if (_closing || state.Owners.Count == 0)
            {
                CloseWatcher(watcher);
                return;
            }
            state.Watcher = watcher;
            state.Unhealthy = false;
        }
        catch (Exception error)
        {
            if (!_closing)
            {
                state.Unhealthy = true;
                Warn($"skill-filesystem: failed to watch {state.Root.Path}: {error.Message}");
            }
            throw;
        }
    }

    private WatchHandle? OpenStableWatcher(RootWatchState state)
    {
        while (!_closing && state.Owners.Count > 0)
        {
            var mode = ResolveRootWatchMode(state.Root.Path, _config.FollowSymlinks);
            var watcher = mode is RootWatchMode.Ancestor ancestor
                ? OpenAncestorWatcher(state, ancestor)
                : OpenRootWatcher(state, (RootWatchMode.Root)mode);
            var current = ResolveRootWatchMode(state.Root.Path, _config.FollowSymlinks);
            if (SameWatchMode(mode, current))
                return watcher;
            CloseWatcher(watcher);
        }
        return null;
    }

    private WatchHandle OpenAncestorWatcher(RootWatchState state, RootWatchMode.Ancestor mode)
        => new AncestorWatchHandle(mode, _config.PollIntervalMs, () => HandleAncestorWatchEvent(state, mode));

    private void HandleAncestorWatchEvent(RootWatchState state, RootWatchMode.Ancestor mode)
        => _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                RootWatchMode current;
                try
                {
                    current = ResolveRootWatchMode(state.Root.Path, _config.FollowSymlinks);
                }
                catch (Exception error)
                {
                    if (!_closing && state.Owners.Count > 0)
                        HandleWatcherError(state, error);
                    return;
                }
                if (_closing || state.Owners.Count == 0 || SameWatchMode(mode, current))
                    return;
                state.Unhealthy = true;
            }
            finally
            {
                _gate.Release();
            }
            QueueInvalidation();
            ScheduleRewatch(state);
        });

    private WatchHandle OpenRootWatcher(RootWatchState state, RootWatchMode.Root mode)
    {
        var watcher = new FileSystemWatcher(mode.Anchor)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
        };
        watcher.Created += (_, e) => OnWatcherEvent(state, mode, e.FullPath, WatchEvents.Add, WatchEvents.AddDir);
        watcher.Changed += (_, e) => OnWatcherEvent(state, mode, e.FullPath, WatchEvents.Change);
        watcher.Deleted += (_, e) => OnWatcherEvent(state, mode, e.FullPath, WatchEvents.Unlink, WatchEvents.UnlinkDir);
        watcher.Renamed += (_, e) =>
        {
            OnWatcherEvent(state, mode, e.OldFullPath, WatchEvents.Unlink, WatchEvents.UnlinkDir);
            OnWatcherEvent(state, mode, e.FullPath, WatchEvents.Add, WatchEvents.AddDir, WatchEvents.Change);
        };
        watcher.Error += (_, e) => HandleWatcherError(state, e.GetException());
        var handle = new RootWatchHandle(mode, watcher);
        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            handle.Close();
            throw;
        }
        return handle;
    }

    private void OnWatcherEvent(RootWatchState state, RootWatchMode.Root mode, string fullPath, params string[] events)
    {
        if (_closing)
            return;
        var target = Path.GetFullPath(fullPath);
        var filterRoot = state.Root with { Path = mode.Anchor };
        if (!events.Any(eventName => IsRelevantWatchEvent(filterRoot, eventName, target)))
            return;
        DebouncedInvalidate();
        if (PathEquals(target, mode.Anchor) && events.Contains(WatchEvents.UnlinkDir))
        {
            state.Unhealthy = true;
            ScheduleRewatch(state);
        }
    }

    private void HandleWatcherError(RootWatchState state, Exception error)
    {
        if (_closing)
            return;
        Warn($"skill-filesystem: watcher for {state.Root.Path} failed: {error.Message}");
        state.Unhealthy = true;
        QueueInvalidation();
        ScheduleRewatch(state);
    }

    private void ScheduleRewatch(RootWatchState state)
        => _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                try
                {
                    EnsureWatcher(state);
                }
                catch (Exception error)
                {
                    Warn($"skill-filesystem: rewatch of {state.Root.Path} failed: {error.Message}");
                    return;
                }
            }
            finally
            {
                _gate.Release();
            }
            QueueInvalidation();
        });

    private void DebouncedInvalidate()
    {
        lock (_debounceSync)
        {
            _debounceTimer?.Dispose();
            if (_closing)
                return;
            _debounceTimer = new Timer(_ => QueueInvalidation(), null, _config.StabilityThresholdMs, Timeout.Infinite);
        }
    }

    private void QueueInvalidation()
    {
        if (_closing || Interlocked.Exchange(ref _invalidationQueued, 1) != 0)
            return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Interlocked.Exchange(ref _invalidationQueued, 0);
            if (_closing)
                return;
            _invalidate();
        });
    }

    private void CloseWatcher(WatchHandle watcher)
    {
        try
        {
            watcher.Close();
        }
        catch (Exception error)
        {
            Warn($"skill-filesystem: failed to close watcher: {error.Message}");
        }
    }

    private static RootWatchMode ResolveRootWatchMode(string root, bool followSymlinks)
    {
        var candidate = root;
        while (true)
        {
            if (Directory.Exists(candidate))
            {
                var preserveRootLink = candidate == root && !followSymlinks && IsLink(candidate);
                var anchor = preserveRootLink ? Path.GetFullPath(candidate) : HomePaths.CanonicalizeWatchPath(candidate);
                if (candidate == root)
                    return new RootWatchMode.Root(anchor);
                var firstSegment = Path.GetRelativePath(candidate, root).Split(Path.DirectorySeparatorChar)[0];
                if (firstSegment.Length == 0)
                    return new RootWatchMode.Root(anchor);
                return new RootWatchMode.Ancestor(anchor, Path.Combine(anchor, firstSegment));
            }
            var parent = Path.GetDirectoryName(candidate);
            if (parent is null || parent == candidate)
                return new RootWatchMode.Ancestor(candidate, root);
            candidate = parent;
        }
    }

    private static bool IsLink(string path)
        => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static bool SameWatchMode(RootWatchMode left, RootWatchMode right)
        => (left, right) switch
        {
            (RootWatchMode.Root l, RootWatchMode.Root r) => l.Anchor == r.Anchor,
            (RootWatchMode.Ancestor l, RootWatchMode.Ancestor r) => l.Anchor == r.Anchor && l.NextPath == r.NextPath,
            _ => false,
        };

    internal static bool IsRelevantWatchEvent(SkillRoot root, string eventName, string path)
    {
        var segments = ContainedSegments(root.Path, path);
        if (segments is null)
            return false;
        if (segments.Length == 0)
            return eventName is WatchEvents.AddDir or WatchEvents.UnlinkDir;
        if (root.SkipSystem && segments[0] == FileSystemSkillProvider.SystemDirName)
            return false;
        if (segments.Length == 1)
        {
            if (eventName is WatchEvents.AddDir or WatchEvents.UnlinkDir)
                return true;
            return segments[0].EndsWith(".md", StringComparison.Ordinal);
        }
        return segments.Length == 2
            && segments[1] == FileSystemSkillProvider.SkillFileName
            && eventName is not (WatchEvents.AddDir or WatchEvents.UnlinkDir);
    }

    internal static bool IsPotentialSkillPath(SkillRoot root, string path)
    {
        var segments = ContainedSegments(root.Path, path);
        if (segments is null || segments.Length == 0 || segments.Length > 2)
            return false;
        if (root.SkipSystem && segments[0] == FileSystemSkillProvider.SystemDirName)
            return false;
        return segments.Length == 1
            ? segments[0].EndsWith(".md", StringComparison.Ordinal)
            : segments[1] == FileSystemSkillProvider.SkillFileName;
    }

    private static string[]? ContainedSegments(string root, string path)
    {
        var child = Path.GetRelativePath(root, path);
        if (child.Length == 0 || child == ".")
            return [];
        if (child == ".." || child.StartsWith($"..{Path.DirectorySeparatorChar}") || Path.IsPathRooted(child))
            return null;
        return child.Split(Path.DirectorySeparatorChar);
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void Warn(string message) => _ctx.LoggerFor(SkillFilesystem.PluginName).Warn(message);
}
