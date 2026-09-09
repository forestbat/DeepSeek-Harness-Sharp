using System.Reflection;
using Cordis;

namespace Dsh.Plugins;

public sealed class PluginCatalog
{
    private readonly Dictionary<string, Type> _plugins = new();

    public IReadOnlyCollection<string> PackageNames => _plugins.Keys;

    public void RegisterAssembly(Assembly assembly)
    {
        var attributes = assembly.GetCustomAttributes<DshPluginAttribute>().ToList();
        if (attributes.Count == 0)
            return;
        var pluginType = FindPluginType(assembly);
        foreach (var attribute in attributes)
        {
            if (_plugins.TryGetValue(attribute.PackageName, out var existing))
            {
                throw new InvalidOperationException(
                    $"Plugin package '{attribute.PackageName}' is already registered by {existing.FullName}; cannot also register {pluginType.FullName}.");
            }
            _plugins[attribute.PackageName] = pluginType;
        }
    }

    public bool TryCreate(string packageName, out IDshPlugin? plugin)
    {
        if (_plugins.TryGetValue(packageName, out var pluginType))
        {
            plugin = CreatePlugin(pluginType, packageName);
            return true;
        }
        plugin = null;
        return false;
    }

    public bool TryCreateDefinition(string packageName, out PluginDefinition? definition)
    {
        if (!_plugins.TryGetValue(packageName, out var pluginType))
        {
            definition = null;
            return false;
        }
        definition = CreateDefinition(pluginType, packageName);
        return true;
    }

    public PluginDefinition CreateDefinition(string packageName)
    {
        if (!_plugins.TryGetValue(packageName, out var pluginType))
        {
            throw new KeyNotFoundException($"Plugin package '{packageName}' is not registered.");
        }
        return CreateDefinition(pluginType, packageName);
    }

    private static Type FindPluginType(Assembly assembly)
    {
        var types = LoadTypes(assembly);
        var pluginTypes = types
            .Where(type => typeof(IDshPlugin).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
            .ToList();
        if (pluginTypes.Count != 1)
        {
            throw new InvalidOperationException(
                $"Assembly '{assembly.FullName}' must contain exactly one IDshPlugin implementation to be registered as a plugin, found {pluginTypes.Count}.");
        }
        return pluginTypes[0];
    }

    private static IReadOnlyList<Type> LoadTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>().ToList();
        }
    }

    private static PluginDefinition CreateDefinition(Type pluginType, string packageName)
    {
        var instance = CreatePlugin(pluginType, packageName);
        return new PluginDefinition
        {
            Name = packageName,
            Inject = instance.Inject.ToDictionary(name => name, _ => (object?)null),
            Callback = new DelegatePluginCallback((ctx, config) =>
            {
                var plugin = CreatePlugin(pluginType, packageName);
                var registration = plugin.Apply(ctx, config);
                return (Action)(() => registration.Dispose());
            }),
        };
    }

    private static IDshPlugin CreatePlugin(Type pluginType, string packageName)
    {
        if (pluginType.GetConstructor([typeof(string)]) is not null)
            return (IDshPlugin)Activator.CreateInstance(pluginType, packageName)!;
        return (IDshPlugin)Activator.CreateInstance(pluginType)!;
    }
}
