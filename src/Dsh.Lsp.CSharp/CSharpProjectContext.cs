using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynWorkspace = Microsoft.CodeAnalysis.Workspace;

namespace Dsh.Lsp.CSharp;

public sealed class CSharpProjectContext
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<CSharpProjectContext>>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static int _msbuildInitialized;
    public static string? LastError { get; private set; }

    public string Root { get; }
    public string ProjectPath { get; }
    public bool IsMsBuild { get; }
    public IReadOnlyList<string> SourceFiles { get; }
    public IReadOnlyList<MetadataReference> References => Compilation.References.ToArray();
    public CSharpCompilation Compilation { get; }
    public Project? RoslynProject { get; }
    public RoslynWorkspace? Workspace { get; }

    private CSharpProjectContext(
        string root,
        string projectPath,
        bool isMsBuild,
        IReadOnlyList<string> sourceFiles,
        CSharpCompilation compilation,
        Project? roslynProject,
        RoslynWorkspace? workspace)
    {
        Root = root;
        ProjectPath = projectPath;
        IsMsBuild = isMsBuild;
        SourceFiles = sourceFiles;
        Compilation = compilation;
        RoslynProject = roslynProject;
        Workspace = workspace;
    }

    public static Task<CSharpProjectContext> LoadAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        var projectPath = FindProject(root);
        if (projectPath is not null)
            root = Path.GetDirectoryName(projectPath) ?? root;
        var stamp = Stamp(root);
        return Cache.GetOrAdd(
            $"{root}|{stamp}",
            _ => new Lazy<Task<CSharpProjectContext>>(
                () => LoadCoreAsync(root, projectPath, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public CSharpCompilation CreateCompilation(SyntaxTree? overrideTree = null)
    {
        if (overrideTree is null)
            return Compilation;
        var trees = Compilation.SyntaxTrees
            .Where(tree => !string.Equals(tree.FilePath, overrideTree.FilePath, StringComparison.OrdinalIgnoreCase))
            .Append(overrideTree);
        return Compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(trees);
    }

    private static async Task<CSharpProjectContext> LoadCoreAsync(
        string root,
        string? projectPath,
        CancellationToken cancellationToken)
    {
        if (projectPath is not null
            && await TryLoadMsBuildAsync(root, projectPath, cancellationToken).ConfigureAwait(false) is { } msbuild)
        {
            return msbuild;
        }

        var files = EnumerateSources(root);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = new List<SyntaxTree>(files.Count);
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file, cancellationToken: cancellationToken));
        }

        var references = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray()
            ?? [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(projectPath ?? "Dsh.Lsp.CSharp.virtual"),
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new CSharpProjectContext(
            root,
            projectPath ?? Path.Combine(root, "Dsh.Lsp.CSharp.virtual.csproj"),
            false,
            files,
            compilation,
            null,
            null);
    }

    private static async Task<CSharpProjectContext?> TryLoadMsBuildAsync(
        string root,
        string projectPath,
        CancellationToken cancellationToken)
    {
        if (!EnsureMsBuild())
        {
            LastError = "MSBuild locator could not find a .NET SDK";
            return null;
        }
        MSBuildWorkspace? workspace = null;
        try
        {
            workspace = MSBuildWorkspace.Create();
            var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested
                && await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is CSharpCompilation compilation)
            {
                var sourceFiles = compilation.SyntaxTrees
                    .Select(tree => tree.FilePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new CSharpProjectContext(
                    root,
                    projectPath,
                    true,
                    sourceFiles,
                    compilation,
                    project,
                    workspace);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LastError = exception.Message;
        }

        workspace?.Dispose();
        return null;
    }

    private static bool EnsureMsBuild()
    {
        if (Volatile.Read(ref _msbuildInitialized) != 0)
            return MSBuildLocator.IsRegistered;
        foreach (var sdk in SdkCandidates())
        {
            try
            {
                var dotnetRoot = Directory.GetParent(sdk)?.Parent?.FullName;
                if (dotnetRoot is null)
                    continue;
                ConfigureDotnetRoot(dotnetRoot);
                MSBuildLocator.RegisterMSBuildPath(sdk);
                break;
            }
            catch (Exception)
            {
                LastError = $"MSBuild SDK could not be registered: {sdk}";
            }
        }

        try
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }

        Interlocked.Exchange(ref _msbuildInitialized, 1);
        return MSBuildLocator.IsRegistered;
    }

    private static void ConfigureDotnetRoot(string dotnetRoot)
    {
        Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRoot);
        Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", dotnetRoot);
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", Path.Combine(dotnetRoot, "dotnet.exe"));
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Contains(dotnetRoot, StringComparer.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PATH", $"{dotnetRoot}{Path.PathSeparator}{path}");
        }
    }

    private static IEnumerable<string> SdkCandidates()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"),
        };
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            foreach (var sdk in EnumerateSdks(root!))
                yield return sdk;
        }

        var jetBrains = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "JetBrains");
        if (Directory.Exists(jetBrains))
        {
            foreach (var installation in Directory.EnumerateDirectories(jetBrains, "JetBrains Rider*"))
            {
                var sdkRoot = Path.Combine(installation, "lib", "ReSharperHost", "windows-x64", "dotnet", "sdk");
                foreach (var sdk in EnumerateSdks(sdkRoot))
                    yield return sdk;
            }
        }
    }

    private static IEnumerable<string> EnumerateSdks(string sdkRoot)
    {
        if (!Directory.Exists(sdkRoot))
            yield break;
        foreach (var sdk in Directory.EnumerateDirectories(sdkRoot).OrderDescending())
        {
            if (File.Exists(Path.Combine(sdk, "MSBuild.dll")))
                yield return sdk;
        }
    }

    private static IReadOnlyList<string> EnumerateSources(string root)
    {
        if (!Directory.Exists(root))
            return [];
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var relative = Path.GetRelativePath(root, file);
                return !relative.StartsWith($"bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith($"obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
            })
            .Take(500)
            .ToArray();
    }

    private static string Stamp(string root)
    {
        if (!Directory.Exists(root))
            return "0";
        var latest = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var relative = Path.GetRelativePath(root, file);
                var extension = Path.GetExtension(file);
                return extension is ".cs" or ".csproj" or ".props" or ".targets"
                    || relative.EndsWith(Path.Combine("obj", "project.assets.json"), StringComparison.OrdinalIgnoreCase);
            })
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        return latest.Ticks.ToString();
    }

    private static string? FindProject(string path)
    {
        var directory = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path).Directory;
        while (directory is not null)
        {
            if (directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || directory.Name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                break;
            var project = Directory.EnumerateFiles(directory.FullName, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (project is not null)
                return project;
            directory = directory.Parent;
        }

        return null;
    }
}
