using Cordis;

namespace Dsh.Skills;

public sealed record SkillFilesystemConfig
{
    public string ProviderName { get; init; } = FileSystemSkillProvider.DefaultProviderName;
    public bool IncludeDefaultRoots { get; init; } = true;
    public string? DshHome { get; init; }
    public string? AgentsHome { get; init; }
    public IReadOnlyList<string>? CustomSkillDirs { get; init; }
    public bool Watch { get; init; } = true;
    public bool WatchUsePolling { get; init; }
    public int WatchStabilityThresholdMs { get; init; } = SkillWatchManager.DefaultStabilityThresholdMs;
    public int WatchPollIntervalMs { get; init; } = SkillWatchManager.DefaultPollIntervalMs;
    public int WatchMaxProjects { get; init; } = SkillWatchManager.DefaultMaxProjects;
    public bool WatchFollowSymlinks { get; init; } = true;
    public string? BundledSkillDir { get; init; }
}

public sealed record ParsedSkill(
    string Name,
    string Description,
    string? WhenToUse,
    SkillInvocationPolicy Invocation,
    IReadOnlyDictionary<string, object?>? Metadata,
    string Content);

internal sealed record SkillRoot(
    string Path,
    string Source,
    int Rank,
    bool SkipSystem = false,
    string? ProjectRoot = null,
    bool TrustedHost = false);

internal sealed record SkillRootEntry(string Name, string Type, string Path);

internal sealed record LocalLocator(string Path, string Directory);

public sealed class FileSystemSkillProvider : ISkillProvider
{
    public const string DefaultProviderName = "filesystem";

    internal const int ProjectDshRank = 100;
    internal const int ProjectAgentsRank = 200;
    internal const int CustomRank = 300;
    internal const int UserDshRank = 400;
    internal const int UserAgentsRank = 500;

    internal const string SystemDirName = ".system";
    internal const string SkillFileName = "SKILL.md";

    private const string DshAgentsHomeEnv = "DSH_AGENTS_HOME";
    private const string BundledSkillDirEnv = "DSH_BUNDLED_SKILL_DIR";

    private readonly Context _ctx;
    private readonly bool _includeDefaultRoots;
    private readonly string _dshHome;
    private readonly string _agentsHome;
    private readonly IReadOnlyList<string> _customSkillDirs;
    private readonly string? _bundledSkillDir;
    private readonly SkillWatchManager _watchManager;
    private Task? _disposal;

    public FileSystemSkillProvider(Context ctx, SkillProviderControl control, SkillFilesystemConfig? config = null)
    {
        config ??= new SkillFilesystemConfig();
        _ctx = ctx;
        Name = config.ProviderName;
        _includeDefaultRoots = config.IncludeDefaultRoots;
        _dshHome = HomePaths.ResolveDshHome(config.DshHome);
        _agentsHome = Path.GetFullPath(
            config.AgentsHome
            ?? Environment.GetEnvironmentVariable(DshAgentsHomeEnv)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents"));
        _customSkillDirs = (config.CustomSkillDirs ?? []).Select(Path.GetFullPath).ToList();
        _watchManager = new SkillWatchManager(ctx, control.Invalidate, SkillWatchManager.ResolveConfig(config));
        control.Signal.Register(() => _ = Dispose());
        var bundledSkillDir = config.BundledSkillDir
            ?? (_includeDefaultRoots ? Environment.GetEnvironmentVariable(BundledSkillDirEnv) : null);
        _bundledSkillDir = bundledSkillDir is null ? null : Path.GetFullPath(bundledSkillDir);
    }

    public string Name { get; }

    public async Task<SkillProviderObservation> List(SkillLookupOptions options)
    {
        var roots = await Roots(options.Cwd);
        var complete = true;
        try
        {
            await _watchManager.ObserveRoots(roots);
        }
        catch
        {
            if (_disposal is not null)
                throw;
            complete = false;
        }
        var candidates = new List<SkillCandidate>();
        foreach (var root in roots)
            candidates.AddRange(await DiscoverRoot(root));
        return new SkillProviderObservation(candidates, complete);
    }

    public async Task<SkillDefinition?> Get(SkillCandidate candidate, SkillLookupOptions options)
    {
        var locator = (LocalLocator)candidate.Locator!;
        var parsed = await ParseSkillFile(locator.Path, options.Signal, candidate.Source == SkillSources.Bundled);
        if (parsed is null)
            return null;
        return new SkillDefinition
        {
            Name = parsed.Name,
            Description = parsed.Description,
            WhenToUse = parsed.WhenToUse,
            Invocation = parsed.Invocation,
            Source = candidate.Source,
            Provider = Name,
            ResourceBase = new SkillResourceBase.Directory(locator.Directory),
            Path = locator.Path,
            Metadata = parsed.Metadata,
            Content = parsed.Content,
        };
    }

    public void ObserveHostMutation(string path) => _watchManager.ObserveHostMutation(path);

    public Task Dispose() => _disposal ??= _watchManager.Dispose();

    private async Task<IReadOnlyList<SkillRoot>> Roots(string? cwd)
    {
        var roots = new List<SkillRoot>();
        if (_includeDefaultRoots && cwd is not null)
        {
            var projectRoot = await FindProjectRoot(Path.GetFullPath(cwd));
            roots.Add(new SkillRoot(Path.Combine(projectRoot, ".dsh", "skills"), SkillSources.ProjectDsh, ProjectDshRank, ProjectRoot: projectRoot));
            roots.Add(new SkillRoot(Path.Combine(projectRoot, ".agents", "skills"), SkillSources.ProjectAgents, ProjectAgentsRank, ProjectRoot: projectRoot));
        }
        roots.AddRange(_customSkillDirs.Select(path => new SkillRoot(path, SkillSources.Custom, CustomRank)));
        if (_includeDefaultRoots)
        {
            roots.Add(new SkillRoot(Path.Combine(_dshHome, "skills"), SkillSources.UserDsh, UserDshRank, SkipSystem: true));
            roots.Add(new SkillRoot(Path.Combine(_agentsHome, "skills"), SkillSources.UserAgents, UserAgentsRank));
        }
        if (_bundledSkillDir is not null)
            roots.Add(new SkillRoot(_bundledSkillDir, SkillSources.Bundled, SkillRegistry.BundledSkillRank, TrustedHost: true));
        return roots;
    }

    private async Task<IReadOnlyList<SkillCandidate>> DiscoverRoot(SkillRoot root)
    {
        var skills = new List<SkillCandidate>();
        var entries = await ListSkillRootEntries(root);
        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            if (root.SkipSystem && entry.Name == ".system")
                continue;
            var locator = entry.Type == SkillFsEntryTypes.Directory
                ? new LocalLocator(Path.Combine(entry.Path, "SKILL.md"), entry.Path)
                : entry.Type == SkillFsEntryTypes.File && entry.Name.EndsWith(".md")
                    ? new LocalLocator(entry.Path, root.Path)
                    : null;
            if (locator is null)
                continue;
            var parsed = await ParseSkillFile(locator.Path, default, root.TrustedHost);
            if (parsed is null)
                continue;
            skills.Add(new SkillCandidate
            {
                Name = parsed.Name,
                Description = parsed.Description,
                WhenToUse = parsed.WhenToUse,
                Invocation = parsed.Invocation,
                Provider = Name,
                Source = root.Source,
                Rank = root.Rank,
                Locator = locator,
                ResourceBase = new SkillResourceBase.Directory(locator.Directory),
                Path = locator.Path,
                Metadata = parsed.Metadata,
            });
        }
        return skills;
    }

    private async Task<IReadOnlyList<SkillRootEntry>> ListSkillRootEntries(SkillRoot root)
    {
        var fs = OptionalFileSystem();
        if (fs is not null && !root.TrustedHost)
            return await ListSkillRootEntriesFromFileSystem(root, fs);
        return ListSkillRootEntriesFromNode(root);
    }

    private static async Task<IReadOnlyList<SkillRootEntry>> ListSkillRootEntriesFromFileSystem(SkillRoot root, ISkillFs fs)
    {
        try
        {
            var target = await fs.Resolve(root.Path);
            return (await fs.ListDir(target))
                .Select(entry => new SkillRootEntry(entry.Name, entry.Type, entry.Target.DisplayPath))
                .ToList();
        }
        catch (Exception error) when (IsAbsentSkillPathError(error))
        {
            return [];
        }
    }

    private IReadOnlyList<SkillRootEntry> ListSkillRootEntriesFromNode(SkillRoot root)
    {
        IReadOnlyList<string> paths;
        try
        {
            paths = Directory.EnumerateFileSystemEntries(root.Path).ToList();
        }
        catch (Exception error) when (IsAbsentSkillPathError(error))
        {
            return [];
        }
        var result = new List<SkillRootEntry>();
        foreach (var path in paths)
            result.Add(new SkillRootEntry(Path.GetFileName(path), NodeEntryKind(path), path));
        return result;
    }

    private string NodeEntryKind(string fullPath)
    {
        try
        {
            var attributes = File.GetAttributes(fullPath);
            return attributes.HasFlag(FileAttributes.Directory) ? SkillFsEntryTypes.Directory : SkillFsEntryTypes.File;
        }
        catch (Exception error) when (IsAbsentSkillPathError(error) || error is IOException or UnauthorizedAccessException)
        {
            Warn($"skill entry {fullPath} ignored: failed to follow symbolic link: {error.Message}");
            return SkillFsEntryTypes.Other;
        }
    }

    private async Task<ParsedSkill?> ParseSkillFile(string path, CancellationToken signal, bool trustedHost)
    {
        var raw = await ReadSkillText(path, signal, trustedHost);
        signal.ThrowIfCancellationRequested();
        if (raw is null)
            return null;
        ParsedSkillFrontmatter? parsed;
        try
        {
            parsed = SkillFrontmatter.Parse(raw);
        }
        catch (Exception error)
        {
            Warn($"skill file {path} ignored: invalid YAML frontmatter: {error.Message}");
            return null;
        }
        if (parsed is null)
        {
            Warn($"skill file {path} ignored: missing YAML frontmatter");
            return null;
        }
        var name = SkillFrontmatter.StringField(parsed.Data, "name");
        var description = SkillFrontmatter.StringField(parsed.Data, "description");
        if (name is null || description is null)
        {
            Warn($"skill file {path} ignored: frontmatter requires name and description");
            return null;
        }
        if (!SkillRegistry.IsSkillName(name))
        {
            Warn($"skill file {path} ignored: invalid skill name \"{name}\"");
            return null;
        }
        SkillInvocationPolicy invocation;
        try
        {
            invocation = SkillFrontmatter.ParseInvocationPolicy(parsed.Data);
        }
        catch (Exception error)
        {
            Warn($"skill file {path} ignored: invalid invocation frontmatter: {error.Message}");
            return null;
        }
        return new ParsedSkill(
            name,
            description,
            SkillFrontmatter.StringField(parsed.Data, "whenToUse"),
            invocation,
            SkillFrontmatter.OptionalMetadata(parsed.Data),
            parsed.Body.Trim());
    }

    private ISkillFs? OptionalFileSystem() => _ctx.Get<ISkillFs>("fs", false);

    private async Task<string?> ReadSkillText(string path, CancellationToken signal, bool trustedHost)
    {
        signal.ThrowIfCancellationRequested();
        var fs = OptionalFileSystem();
        if (fs is not null && !trustedHost)
            return await ReadSkillTextFromFileSystem(fs, path, signal);
        try
        {
            return await File.ReadAllTextAsync(path, signal);
        }
        catch (Exception error)
        {
            signal.ThrowIfCancellationRequested();
            if (IsAbsentSkillPathError(error))
                return null;
            throw;
        }
    }

    private async Task<string?> ReadSkillTextFromFileSystem(ISkillFs fs, string path, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        SkillFsTarget target;
        try
        {
            target = await fs.Resolve(path);
        }
        catch (Exception error)
        {
            if (IsAbsentSkillPathError(error))
                return null;
            throw;
        }
        signal.ThrowIfCancellationRequested();
        SkillFsInfo? info;
        try
        {
            info = await fs.Stat(target, signal);
        }
        catch (Exception error)
        {
            signal.ThrowIfCancellationRequested();
            if (IsAbsentSkillPathError(error))
                return null;
            throw;
        }
        if (info is null || info.Type != SkillFsEntryTypes.File)
            return null;
        try
        {
            return await fs.ReadText(target, signal);
        }
        catch (Exception error)
        {
            signal.ThrowIfCancellationRequested();
            if (IsAbsentSkillPathError(error))
                return null;
            if (error is not SkillFsException { Code: SkillFsException.NotText })
                throw;
            Warn($"skill file {path} ignored: failed to read text file at {target.DisplayPath}: {error.Message}");
            return null;
        }
    }

    private async Task<string> FindProjectRoot(string cwd)
    {
        var current = cwd;
        while (true)
        {
            if (await PathExists(Path.Combine(current, ".git")))
                return current;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
                return cwd;
            current = parent;
        }
    }

    private async Task<bool> PathExists(string path)
    {
        var fs = OptionalFileSystem();
        if (fs is null)
            return File.Exists(path) || Directory.Exists(path);
        SkillFsTarget target;
        try
        {
            target = await fs.Resolve(path);
        }
        catch
        {
            return false;
        }
        try
        {
            return await fs.Stat(target, default) is not null;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsAbsentPathError(Exception error)
        => error is FileNotFoundException or DirectoryNotFoundException
            || error is IOException { HResult: var hResult } && (hResult & 0xFFFF) == 267;

    internal static bool IsAbsentSkillPathError(Exception error)
        => IsAbsentPathError(error) || error is SkillFsException { IsAbsent: true };

    private void Warn(string message) => _ctx.LoggerFor(SkillFilesystem.PluginName).Warn(message);
}

public static class SkillFilesystem
{
    public const string PluginName = "skill-filesystem";
    public const string ObservedEvent = "fs/observed";

    public static IDisposable Apply(Context ctx, SkillFilesystemConfig? config = null)
    {
        var skills = ctx.Get<SkillRegistry>(SkillRegistry.ServiceName)!;
        FileSystemSkillProvider? provider = null;
        var registration = skills.RegisterProvider(control =>
        {
            provider = new FileSystemSkillProvider(ctx, control, config);
            return provider;
        });
        var effect = ctx.Effect(() => (Func<Task>)(async () =>
        {
            if (provider is not null)
                await provider.Dispose();
        }), "skill-filesystem watcher");
        var observed = ctx.On(ObservedEvent, (_, args) =>
        {
            if (args.Length >= 3 && args[0] is SkillFsTarget target && MutationToolName(args[2]) is not null)
                provider?.ObserveHostMutation(target.DisplayPath);
            return new ValueTask<object?>();
        });
        return new SkillFilesystemRegistration(registration, effect, observed);
    }

    private static string? MutationToolName(object? actor)
        => actor is IReadOnlyDictionary<string, object?> fields
            && fields.TryGetValue("name", out var value)
            && value is string name
            && name is "edit" or "write"
                ? name
                : null;

    private sealed class SkillFilesystemRegistration(IDisposable registration, EffectHandle effect, Func<bool> observed) : IDisposable
    {
        public void Dispose()
        {
            registration.Dispose();
            effect.Dispose();
            observed();
        }
    }
}
