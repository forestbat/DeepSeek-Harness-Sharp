using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Dsh.Sandbox;

/// <summary>Read-only code and runtime directory roots used by sandboxed processes.</summary>
public static class SandboxRuntimePaths
{
    private static readonly Lazy<IReadOnlyList<string>> CurrentHost = new(BuildCurrentHost);
    private static readonly string? UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
        ? Path.TrimEndingDirectorySeparator(global::Dsh.Transport.FilePathAccess.Canonicalize(home)) : null;

    public static IReadOnlyList<string> ForCurrentHost() => CurrentHost.Value;

    public static IReadOnlyList<string> ForExecutable(string executable)
    {
        var paths = NewSet();
        paths.UnionWith(ForCurrentHost());
        var full = Path.GetFullPath(executable);
        paths.Add(full);
        var resolved = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        paths.Add(resolved);
        var muxer = Path.GetFileNameWithoutExtension(resolved).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (!muxer)
        {
            AddDirectory(paths, Path.GetDirectoryName(full));
            AddDirectory(paths, Path.GetDirectoryName(resolved));
        }
        var dotnet = muxer ? Path.GetDirectoryName(resolved) : DotnetRoot();
        AddDotnet(paths, dotnet, includeSdk: true);
        return paths.ToArray();
    }

    private static IReadOnlyList<string> BuildCurrentHost()
    {
        var paths = NewSet();
        if (Environment.ProcessPath is { } executable)
            paths.Add(Path.GetFullPath(executable));
        AddDirectory(paths, AppContext.BaseDirectory);
        AddDirectory(paths, RuntimeEnvironment.GetRuntimeDirectory());
        AddDotnet(paths, DotnetRoot(), includeSdk: false);
        if (!OperatingSystem.IsWindows())
        {
            foreach (var path in new[] { "/bin", "/usr/bin", "/sbin", "/usr/sbin", "/lib", "/lib64", "/usr/lib", "/usr/lib64" })
                AddDirectory(paths, path);
            if (OperatingSystem.IsMacOS())
            {
                AddDirectory(paths, "/System/Library/Frameworks");
                AddDirectory(paths, "/System/Library/PrivateFrameworks");
                if (File.Exists("/usr/lib/dyld"))
                    paths.Add("/usr/lib/dyld");
            }
        }
        if (SandboxedProcess.BackendExecutablePath is { } backend)
            paths.Add(backend);
        return paths.ToArray();
    }

    private static void AddDotnet(HashSet<string> paths, string? root, bool includeSdk)
    {
        if (root is null)
            return;
        foreach (var child in includeSdk ? new[] { "host", "shared", "sdk", "packs", "sdk-manifests" } : new[] { "host", "shared" })
            AddDirectory(paths, Path.Combine(root, child));
        var muxer = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (File.Exists(muxer))
            paths.Add(muxer);
    }

    private static void AddDirectory(HashSet<string> paths, string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
            return;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var canonical = global::Dsh.Transport.FilePathAccess.Canonicalize(full);
        if (paths.Comparer.Equals(canonical, Path.GetPathRoot(canonical))
            || UserHome is not null && paths.Comparer.Equals(canonical, UserHome))
            return;
        paths.Add(full);
    }

    public static string? DotnetRoot()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return null;
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        return runtime.Parent is { Name: "Microsoft.NETCore.App", Parent: { Name: "shared", Parent: { } root } }
            ? root.FullName : null;
    }

    private static HashSet<string> NewSet()
        => new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
