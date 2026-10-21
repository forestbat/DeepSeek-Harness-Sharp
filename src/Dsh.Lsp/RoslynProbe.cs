using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Lsp;

public static class RoslynProbe
{
    private const string AdapterName = "Dsh.Lsp.CSharp.dll";

    public static IDisposable? TryLoad(
        Context context,
        out string? error,
        IEnumerable<string>? adapterDirectories = null)
    {
        error = null;
        var candidates = adapterDirectories ?? CandidateDirectories();
        var adapter = candidates
            .Select(directory => Path.Combine(directory, AdapterName))
            .FirstOrDefault(File.Exists);
        if (adapter is null)
            return null;

        var adapterDirectory = Path.GetDirectoryName(adapter)!;
        var roslynDirectories = RoslynDirectories(adapterDirectory).Where(HasRoslyn).ToArray();
        var compatibleDirectories = roslynDirectories
            .Where(directory => CheckCompatibility(adapter, directory) is null)
            .ToArray();
        if (compatibleDirectories.Length == 0)
        {
            error = roslynDirectories
                .Select(directory => CheckCompatibility(adapter, directory))
                .FirstOrDefault(message => message is not null)
                ?? "Roslyn compiler assemblies were not found";
            return null;
        }

        if (AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => assembly.GetName().Name == "Dsh.Lsp.CSharp"))
            return null;

        try
        {
            var searchDirectories = compatibleDirectories
                .Prepend(adapterDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                foreach (var directory in searchDirectories)
                {
                    var candidate = Path.Combine(directory, $"{name.Name}.dll");
                    if (File.Exists(candidate))
                        return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                }

                return null;
            };
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(adapter);
            var pluginType = assembly.GetTypes().FirstOrDefault(type =>
                type.Name == "Plugin" && typeof(IDshPlugin).IsAssignableFrom(type));
            if (pluginType is null)
            {
                error = "Dsh.Lsp.CSharp does not contain a plugin entry point";
                return null;
            }

            var plugin = (IDshPlugin)Activator.CreateInstance(pluginType)!;
            return plugin.Apply(context, null);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }
    }

    public static string? CheckCompatibility(string adapterPath, string roslynDirectory)
    {
        if (!File.Exists(adapterPath))
            return "adapter assembly was not found";
        try
        {
            using var stream = File.OpenRead(adapterPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return "adapter is not a managed assembly";
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                var name = reader.GetString(reference.Name);
                if (!name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
                    continue;
                var path = Path.Combine(roslynDirectory, $"{name}.dll");
                if (!File.Exists(path))
                    return $"{name} was not found";
                var actual = AssemblyName.GetAssemblyName(path).Version;
                if (actual is null || actual < reference.Version)
                    return $"{name} {reference.Version} is required but {actual} was found";
            }

            return null;
        }
        catch (BadImageFormatException)
        {
            return "adapter is not a valid managed assembly";
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "plugins");
        yield return Path.Combine(AppContext.BaseDirectory, "plugins", "lsp-csharp");
        yield return Path.Combine(AppContext.BaseDirectory, "plugins", "dsh-lsp-csharp");
        yield return Path.Combine(AppContext.BaseDirectory, "Dsh.Lsp.CSharp");
    }

    private static IEnumerable<string> RoslynDirectories(string adapterDirectory)
    {
        yield return adapterDirectory;
        yield return Path.Combine(adapterDirectory, "roslyn");
        var dotnetRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var root in dotnetRoots)
        {
            var sdks = Path.Combine(root, ".dotnet", "sdk");
            if (Directory.Exists(sdks))
            {
                foreach (var sdk in Directory.EnumerateDirectories(sdks).OrderDescending())
                {
                    yield return Path.Combine(sdk, "Roslyn", "bincore");
                    foreach (var tool in Directory.EnumerateDirectories(sdk, "*", SearchOption.AllDirectories))
                    {
                        if (HasWorkspaces(tool))
                            yield return tool;
                    }
                }
            }

            var programSdks = Path.Combine(root, "dotnet", "sdk");
            if (Directory.Exists(programSdks))
            {
                foreach (var sdk in Directory.EnumerateDirectories(programSdks).OrderDescending())
                {
                    yield return Path.Combine(sdk, "Roslyn", "bincore");
                    foreach (var tool in Directory.EnumerateDirectories(sdk, "*", SearchOption.AllDirectories))
                    {
                        if (HasWorkspaces(tool))
                            yield return tool;
                    }
                }
            }
        }

        var jetBrains = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "JetBrains");
        if (Directory.Exists(jetBrains))
        {
            foreach (var installation in Directory.EnumerateDirectories(jetBrains, "JetBrains Rider*"))
            {
                var sdks = Path.Combine(installation, "lib", "ReSharperHost", "windows-x64", "dotnet", "sdk");
                if (!Directory.Exists(sdks))
                    continue;
                foreach (var sdk in Directory.EnumerateDirectories(sdks).OrderDescending())
                {
                    yield return Path.Combine(sdk, "Roslyn", "bincore");
                    foreach (var tool in Directory.EnumerateDirectories(sdk, "*", SearchOption.AllDirectories))
                    {
                        if (HasWorkspaces(tool))
                            yield return tool;
                    }
                }
            }
        }
    }

    private static bool HasRoslyn(string directory)
        => File.Exists(Path.Combine(directory, "Microsoft.CodeAnalysis.dll"))
            && File.Exists(Path.Combine(directory, "Microsoft.CodeAnalysis.CSharp.dll"));

    private static bool HasWorkspaces(string directory)
        => File.Exists(Path.Combine(directory, "Microsoft.CodeAnalysis.Workspaces.dll"))
            || File.Exists(Path.Combine(directory, "Microsoft.CodeAnalysis.CSharp.Workspaces.dll"));
}
