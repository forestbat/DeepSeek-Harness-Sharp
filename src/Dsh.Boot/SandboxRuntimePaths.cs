using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

namespace Dsh.Boot;

/// <summary>Read-only code/runtime closures, never an application directory or a user's home.</summary>
internal static class SandboxRuntimePaths
{
    private static readonly Lazy<IReadOnlyList<string>> CurrentHost = new(BuildCurrentHost);
    private static readonly EnumerationOptions CodeEnumeration = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };

    public static IReadOnlyList<string> ForCurrentHost() => CurrentHost.Value;

    public static IReadOnlyList<string> ForExecutable(string executable)
    {
        var paths = NewSet();
        paths.UnionWith(ForCurrentHost());
        var full = Path.GetFullPath(executable);
        paths.Add(full);
        var resolved = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        paths.Add(resolved);
        var toolPayloads = FindToolPayloads(full).Concat(
            string.Equals(full, resolved, StringComparison.Ordinal) ? [] : FindToolPayloads(resolved)).ToArray();
        foreach (var payload in toolPayloads)
            paths.Add(payload);
        if (toolPayloads.Length == 0)
            AddCodeTree(paths, Path.GetDirectoryName(resolved)!, recurse: false);
        if (toolPayloads.Length != 0 || Path.GetFileNameWithoutExtension(resolved).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.GetFileNameWithoutExtension(resolved).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(resolved) : DotnetRoot();
            // Installed SDK/reference-pack trees are immutable runtime inputs, not per-file grants.
            // Keep their roots separate from the installation root, which may contain user data.
            foreach (var child in new[] { "sdk", "packs", "sdk-manifests" })
                if (root is not null && Directory.Exists(Path.Combine(root, child)))
                    paths.Add(Path.Combine(root, child));
        }
        return paths.ToArray();
    }

    private static IReadOnlyList<string> BuildCurrentHost()
    {
        var paths = NewSet();
        if (Environment.ProcessPath is { } executable)
            paths.Add(Path.GetFullPath(executable));
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string assemblies)
            foreach (var assembly in assemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                paths.Add(assembly);
        AddCodeTree(paths, AppContext.BaseDirectory, recurse: true);
        var dotnet = DotnetRoot();
        if (dotnet is not null)
        {
            foreach (var child in new[] { "host", "shared" })
                if (Directory.Exists(Path.Combine(dotnet, child)))
                    paths.Add(Path.Combine(dotnet, child));
            var muxer = Path.Combine(dotnet, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(muxer))
                paths.Add(muxer);
        }
        if (!OperatingSystem.IsWindows())
        {
            // System executable/library directories contain OS code, not user credentials or HOME.
            foreach (var path in new[] { "/bin", "/usr/bin", "/sbin", "/usr/sbin", "/lib", "/lib64", "/usr/lib", "/usr/lib64" })
                if (Directory.Exists(path))
                    paths.Add(path);
            if (OperatingSystem.IsMacOS())
                foreach (var path in new[] { "/System/Library/Frameworks", "/System/Library/PrivateFrameworks", "/usr/lib/dyld" })
                    if (Directory.Exists(path) || File.Exists(path))
                        paths.Add(path);
        }
        if (Dsh.Sandbox.SandboxedProcess.BackendExecutablePath is { } backend)
            paths.Add(backend);
        return paths.ToArray();
    }

    internal static string? DotnetRoot()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return null;
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        return runtime.Parent is { Name: "Microsoft.NETCore.App", Parent: { Name: "shared", Parent: { } root } }
            ? root.FullName : null;
    }

    private static HashSet<string> NewSet()
        => new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static void AddCodeTree(HashSet<string> paths, string directory, bool recurse)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.EnumerateFiles(directory, "*", recurse ? CodeEnumeration : new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        }))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                || name.Contains(".so.", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".runtimeconfig.dev.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tasks", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".rsp", StringComparison.OrdinalIgnoreCase)
                || name.Equals("icudt.dat", StringComparison.OrdinalIgnoreCase)
                || name.Equals("FrameworkList.xml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RuntimeList.xml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PlatformManifest.txt", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PackageOverrides.txt", StringComparison.OrdinalIgnoreCase)
                || name.Equals("KnownWorkloadManifests.txt", StringComparison.OrdinalIgnoreCase)
                || name.Equals("NETCoreSdkRuntimeIdentifierChain.txt", StringComparison.OrdinalIgnoreCase)
                || name.Equals("runtime.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RuntimeIdentifierGraph.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PortableRuntimeIdentifierGraph.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("WorkloadManifest.json", StringComparison.OrdinalIgnoreCase))
                paths.Add(Path.GetFullPath(path));
        }
    }

    private static IEnumerable<string> FindToolPayloads(string executable)
    {
        var parent = Path.GetDirectoryName(executable)!;
        var localMetadata = Path.Combine(parent, "DotnetToolSettings.xml");
        var store = Path.Combine(parent, ".store");
        IEnumerable<string> metadataFiles = File.Exists(localMetadata) ? [localMetadata] : [];
        if (Directory.Exists(store))
            metadataFiles = metadataFiles.Concat(Directory.EnumerateFiles(store, "DotnetToolSettings.xml", CodeEnumeration));
        var command = Path.GetFileNameWithoutExtension(executable);
        foreach (var metadata in metadataFiles)
        {
            using var reader = XmlReader.Create(metadata, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 64 * 1024,
            });
            var document = XDocument.Load(reader);
            foreach (var entry in document.Descendants("Command"))
            {
                if (!string.Equals(entry.Attribute("Name")?.Value, command, StringComparison.OrdinalIgnoreCase))
                    continue;
                var directory = Path.GetDirectoryName(metadata)!;
                var relative = entry.Attribute("EntryPoint")?.Value;
                if (relative is null)
                    continue;
                var target = Path.GetFullPath(relative, directory);
                var within = Path.GetRelativePath(directory, target);
                if (within != ".." && !within.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !Path.IsPathRooted(within) && File.Exists(target))
                {
                    // A shim can select a different TFM/RID from directory enumeration order.
                    // Each matching installed payload is a narrow, read-only code deployment.
                    yield return directory;
                    break;
                }
            }
        }
    }
}
