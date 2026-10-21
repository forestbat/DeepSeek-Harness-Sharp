using System.Reflection;
using System.Runtime.Loader;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Lsp;

public static class CompilerAdapterProbe
{
    public static IDisposable? TryLoad(Context context, string adapterName, out string? error)
    {
        error = null;
        var assemblyName = Path.GetFileNameWithoutExtension(adapterName);
        if (AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => assembly.GetName().Name == assemblyName))
            return null;
        var adapter = CandidateDirectories()
            .Select(directory => Path.Combine(directory, adapterName))
            .FirstOrDefault(File.Exists);
        if (adapter is null)
            return null;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(adapter);
            var pluginType = assembly.GetTypes().FirstOrDefault(type =>
                type.Name == "Plugin" && typeof(IDshPlugin).IsAssignableFrom(type));
            if (pluginType is null)
            {
                error = $"{adapterName} does not contain a plugin entry point";
                return null;
            }

            return ((IDshPlugin)Activator.CreateInstance(pluginType)!).Apply(context, null);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "plugins");
        yield return Path.Combine(AppContext.BaseDirectory, "plugins", "lsp-python");
        yield return Path.Combine(AppContext.BaseDirectory, "Dsh.Lsp.Python");
    }
}
