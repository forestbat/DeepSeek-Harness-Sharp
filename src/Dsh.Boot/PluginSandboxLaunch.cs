using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Dsh.Sandbox;

namespace Dsh.Boot;

/** Only code is exposed to the worker; configuration and business files go through the broker. */
internal static class PluginSandboxLaunch
{
    internal static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan InitializationBudget = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan InspectionBudget = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(10);
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static Dsh.Plugins.PluginForm InspectForm(string? artifactPath)
    {
        if (artifactPath is null)
            return Dsh.Plugins.PluginForm.CompiledIn;
        Dsh.Sandbox.SandboxFileAccess.DemandOrdinaryFile(artifactPath, allowMissing: false);
        if (!Path.GetExtension(artifactPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            return Dsh.Plugins.PluginForm.NativeLibrary;
        try
        {
            using var input = File.OpenRead(artifactPath);
            using var image = new PEReader(input);
            return image.HasMetadata ? Dsh.Plugins.PluginForm.ManagedAssembly : Dsh.Plugins.PluginForm.NativeLibrary;
        }
        catch (BadImageFormatException)
        {
            return Dsh.Plugins.PluginForm.NativeLibrary;
        }
    }

    internal static HashSet<string> CodeClosure(IEnumerable<PluginSandboxSource> sources)
    {
        var paths = new HashSet<string>(SandboxRuntimePaths.ForCurrentHost(), PathComparer);
        foreach (var source in sources)
        {
            if (source.ArtifactPath is not { } artifact)
                continue;
            var full = Path.GetFullPath(artifact);
            RejectLink(full);
            if (!File.Exists(full))
                throw new FileNotFoundException("The isolated plugin artifact does not exist.");
            if (source.Descriptor.Form == Dsh.Plugins.PluginForm.ManagedAssembly)
                AddManaged(full, paths, new AssemblyDependencyResolver(full), SharedDirectory(full), new HashSet<string>(PathComparer));
            else
                PluginSandboxNativeCode.Add(full, paths);
        }
        return paths;
    }

    private static void AddManaged(string path, HashSet<string> paths, AssemblyDependencyResolver resolver, string? shared,
        HashSet<string> visited)
    {
        if (paths.Contains(path) || !visited.Add(path))
            return;
        if (visited.Count > 256)
            throw new InvalidDataException("The managed code dependency closure exceeds its bounded capacity.");
        paths.Add(path);
        RejectLink(path);
        string[] references;
        string[] imports;
        using (var input = File.OpenRead(path))
        using (var image = new PEReader(input))
        {
            if (!image.HasMetadata || image.PEHeaders.CorHeader?.MetadataDirectory.Size is not (> 0 and <= 8 * 1024 * 1024))
                throw new InvalidDataException("The managed plugin metadata is missing or exceeds its bounded capacity.");
            var metadata = image.GetMetadataReader();
            if (metadata.AssemblyReferences.Count > 256)
                throw new InvalidDataException("The managed plugin has too many assembly references.");
            references = metadata.AssemblyReferences.Select(reference =>
                metadata.GetString(metadata.GetAssemblyReference(reference).Name)).ToArray();
            var libraries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in metadata.MethodDefinitions)
            {
                var method = metadata.GetMethodDefinition(handle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                    continue;
                libraries.Add(metadata.GetString(metadata.GetModuleReference(method.GetImport().Module).Name));
                if (libraries.Count > 256)
                    throw new InvalidDataException("The managed plugin has too many native library references.");
            }
            imports = libraries.ToArray();
        }
        // Metadata buffers are released before descending into another artifact.
        foreach (var name in references)
        {
            var resolved = resolver.ResolveAssemblyToPath(new AssemblyName(name));
            resolved ??= ExistingSibling(path, name + ".dll");
            if (resolved is null && shared is not null)
            {
                var candidate = Path.Combine(shared, name + ".dll");
                if (File.Exists(candidate))
                    resolved = candidate;
            }
            if (resolved is not null)
                AddManaged(Path.GetFullPath(resolved), paths, resolver, shared, visited);
        }
        foreach (var name in imports)
        {
            var resolved = resolver.ResolveUnmanagedDllToPath(name);
            PluginSandboxNativeCode.AddImport(path, name, resolved, paths);
        }
        foreach (var suffix in new[] { ".deps.json", ".runtimeconfig.json" })
        {
            var companion = Path.ChangeExtension(path, null) + suffix;
            if (File.Exists(companion))
            {
                RejectLink(companion);
                paths.Add(companion);
            }
        }
    }

    private static string? SharedDirectory(string artifact)
    {
        var directory = Path.GetDirectoryName(artifact)!;
        var candidate = Path.Combine(directory, ".shared");
        if (Directory.Exists(candidate))
            return candidate;
        var parent = Path.GetDirectoryName(directory);
        candidate = parent is null ? null : Path.Combine(parent, ".shared");
        return candidate is not null && Directory.Exists(candidate) ? candidate : null;
    }

    private static string? ExistingSibling(string path, string name)
    {
        var candidate = Path.Combine(Path.GetDirectoryName(path)!, name);
        return File.Exists(candidate) ? candidate : null;
    }

    internal static void RejectLink(string path)
    {
        SandboxFileAccess.DemandOrdinaryFile(path, allowMissing: false);
        var file = new FileInfo(Path.GetFullPath(path));
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Sandbox code artifacts cannot be symbolic links or reparse points.");
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Sandbox code paths cannot traverse symbolic links or reparse points.");
    }

    internal static SandboxProcessOptions Options(IReadOnlyList<string> code)
    {
        var executable = Environment.ProcessPath
            ?? throw new PlatformNotSupportedException("The current host executable cannot be located.");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entry = Environment.GetCommandLineArgs().FirstOrDefault();
            if (string.IsNullOrEmpty(entry) || !File.Exists(entry))
                throw new PlatformNotSupportedException("The managed host entry assembly cannot be located.");
            arguments.Add(Path.GetFullPath(entry));
        }
        arguments.Add(PluginSandboxWorker.HostArgument);
        return new SandboxProcessOptions
        {
            Executable = executable,
            Arguments = arguments,
            RuntimePaths = code,
            WorkingDirectory = Path.GetPathRoot(Path.GetFullPath(executable)),
            AllowNetwork = false,
            AllowChildProcesses = false,
            MemoryLimitBytes = PluginSandboxMethods.WorkerMemoryBytes,
            Environment = new Dictionary<string, string?>
            {
                ["DOTNET_ROOT"] = SandboxRuntimePaths.DotnetRoot(),
                ["DOTNET_EnableDiagnostics"] = "0",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1",
            },
        };
    }
}
