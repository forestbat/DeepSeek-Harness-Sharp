using System.Runtime.Loader;
using System.Collections;
using System.Reflection;
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
                candidate => string.Equals(candidate.GetName().Name, Path.GetFileNameWithoutExtension(fullPath), StringComparison.OrdinalIgnoreCase));
            if (assembly is null)
            {
                try
                {
                    assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
                }
                catch (PlatformNotSupportedException)
                {
                    // NativeAOT 不支持运行时 Assembly.Load*，跳过；插件由生成目录 RegisterGeneratedCatalog 注册。
                    continue;
                }
            }
            if (assembly is not null)
                Catalog.RegisterAssembly(assembly);
        }

        RegisterGeneratedCatalog();
    }

    public void RegisterGeneratedCatalog()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType("Dsh.Plugins.Generated.DshPluginCatalog");
            if (type is null)
                continue;

            var method = type.GetMethod("GetPlugins", BindingFlags.Public | BindingFlags.Static);
            if (method is null)
                continue;

            var entries = (IEnumerable)method.Invoke(null, null)!;
            foreach (var entry in entries)
            {
                var entryType = entry.GetType();
                var package = (string)entryType.GetProperty("Package")!.GetValue(entry)!;
                var implementation = (Type)entryType.GetProperty("Implementation")!.GetValue(entry)!;
                Catalog.RegisterPlugin(package, implementation);
            }

            return;
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
