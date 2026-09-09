using System.Runtime.Loader;
using Cordis.Loader;

namespace Dsh.Plugins;

public sealed class PluginHost
{
    public PluginCatalog Catalog { get; } = new();

    public void ScanDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*.dll"))
        {
            var fileName = Path.GetFileName(file);
            if (!fileName.StartsWith("Dsh.", StringComparison.Ordinal)
                && !fileName.EndsWith(".Plugin.dll", StringComparison.Ordinal))
            {
                continue;
            }
            var fullPath = Path.GetFullPath(file);
            var assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
                candidate => string.Equals(candidate.GetName().Name, Path.GetFileNameWithoutExtension(fullPath), StringComparison.OrdinalIgnoreCase))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
            Catalog.RegisterAssembly(assembly);
        }
    }

    public void RegisterBuiltins(Loader loader)
    {
        foreach (var packageName in Catalog.PackageNames)
        {
            loader.Builtins[packageName] = Catalog.CreateDefinition(packageName);
        }
    }
}
