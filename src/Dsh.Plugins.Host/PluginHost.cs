using System.Reflection;
using System.Runtime.CompilerServices;

namespace Dsh.Plugins;

/** 一次目录扫描的结果:装载的托管插件、持有的原生句柄与逐文件跳过原因。 */
public sealed record PluginScanResult(
    IReadOnlyList<ManagedPlugin> Managed,
    IReadOnlyList<INativePlugin> Native,
    IReadOnlyList<PluginSkip> Skipped);

/** 一次托管装载的结果:登记成功的包、逐项跳过原因、共享依赖声明与持有的加载上下文。 */
public sealed record PluginLoadResult(
    IReadOnlyList<string> Packages,
    IReadOnlyList<PluginSkip> Skipped,
    IReadOnlyList<string> SharedDependencies,
    PluginLoadContext? Context);

/** 托管插件的一次装载:包名、共享依赖声明与持有它的可回收加载上下文。 */
public sealed record ManagedPlugin(string Package, PluginLoadContext Context, IReadOnlyList<string> SharedDependencies);

/** 被跳过的插件制品与原因,供宿主逐文件 WARN。 */
public sealed record PluginSkip(string File, string Reason);

public sealed class PluginHost
{
    private const string ManifestTypeName = "Dsh.Plugins.Generated.DshPluginManifest";
    private const string RegistrationsMethodName = "Registrations";
    private const string SharedDependenciesMethodName = "SharedDependencies";
    private const string SharedPoolDirectoryName = ".shared";

    public PluginCatalog Catalog { get; } = new();

    /** 共享程序集池:由宿主在扫描前挂接,根目录为插件目录下的 .shared。 */
    public SharedAssemblyPool? SharedPool { get; set; }

    /** 镜像内插件:由生成目录在模块初始化时自注册。 */
    public void RegisterCompiledIn()
    {
        foreach (var registration in DshPluginCatalogRegistry.Snapshot())
            Catalog.Register(Descriptor(registration, PluginForm.CompiledIn), registration.Create);
    }

    /** 从文件装载托管插件:按类型名读取插件自带的生成清单,不反射遍历插件类型。 */
    public PluginLoadResult TryLoad(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var context = new PluginLoadContext(fullPath);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(fullPath);
        }
        catch
        {
            context.Unload();
            throw;
        }
        var registrations = ReadManifest(assembly, out var failure);
        if (failure is not null)
        {
            context.Unload();
            return new PluginLoadResult([], [new PluginSkip(fullPath, failure)], [], null);
        }
        if (registrations.Count == 0)
        {
            context.Unload();
            // 宿主镜像程序集的私有副本不随插件打包:没有清单且名字已在镜像里,显式提示删除。
            if (HostImageNames().Contains(Path.GetFileNameWithoutExtension(fullPath)))
                return new PluginLoadResult([], [new PluginSkip(fullPath, "该程序集随宿主镜像分发,不随插件打包;请从插件目录删除")], [], null);
            return new PluginLoadResult([], [], [], null);
        }
        var sharedDependencies = ReadSharedDependencies(assembly);
        context.ConfigureShared(SharedPool, sharedDependencies);
        SharedPool?.AddReferences(sharedDependencies);
        var packages = new List<string>();
        var skipped = new List<PluginSkip>();
        foreach (var registration in registrations)
        {
            if (registration.DescriptorVersion != PluginDescriptor.CurrentVersion)
            {
                skipped.Add(new PluginSkip(fullPath,
                    $"插件描述符版本 {registration.DescriptorVersion} 与宿主 {PluginDescriptor.CurrentVersion} 不兼容"));
                continue;
            }
            try
            {
                Catalog.Register(Descriptor(registration, PluginForm.ManagedAssembly), registration.Create);
            }
            catch (InvalidOperationException) when (Catalog.TryDescribe(registration.Package, out var existing)
                && existing.Form == PluginForm.CompiledIn)
            {
                // 文件形态可替换 compiled-in 登记:镜像内插件的热修复通道;其余重复一律拒绝。
                Catalog.Remove(registration.Package);
                Catalog.Register(Descriptor(registration, PluginForm.ManagedAssembly), registration.Create);
            }
            catch (InvalidOperationException error)
            {
                skipped.Add(new PluginSkip(fullPath, error.Message));
                continue;
            }
            packages.Add(registration.Package);
        }
        if (packages.Count == 0)
        {
            SharedPool?.Release(sharedDependencies);
            context.Unload();
            return new PluginLoadResult(packages, skipped, sharedDependencies, null);
        }
        return new PluginLoadResult(packages, skipped, sharedDependencies, context);
    }

    /** 统一扫描插件目录:每插件一个子目录(兼容期仍读平铺并 WARN);托管进可回收上下文,原生库经 nativeBridge 登记。 */
    public PluginScanResult Scan(string directory, Func<INativePlugin, IDshPlugin>? nativeBridge)
    {
        var packages = new List<string>();
        var managed = new List<ManagedPlugin>();
        var nativePlugins = new List<INativePlugin>();
        var skipped = new List<PluginSkip>();
        if (!Directory.Exists(directory))
            return new PluginScanResult(managed, nativePlugins, skipped);
        var seenAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subdirectory in Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (Path.GetFileName(subdirectory) == SharedPoolDirectoryName)
                continue;
            foreach (var file in Directory.EnumerateFiles(subdirectory).OrderBy(path => path, StringComparer.Ordinal))
                LoadArtifact(file, subdirectory, seenAssemblies, nativeBridge, packages, managed, nativePlugins, skipped, flatLayout: false);
        }
        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(path => path, StringComparer.Ordinal))
            LoadArtifact(file, directory, seenAssemblies, nativeBridge, packages, managed, nativePlugins, skipped, flatLayout: true);
        return new PluginScanResult(managed, nativePlugins, skipped);
    }

    private void LoadArtifact(
        string file,
        string containerDirectory,
        Dictionary<string, string> seenAssemblies,
        Func<INativePlugin, IDshPlugin>? nativeBridge,
        List<string> packages,
        List<ManagedPlugin> managed,
        List<INativePlugin> nativePlugins,
        List<PluginSkip> skipped,
        bool flatLayout)
    {
        var extension = Path.GetExtension(file);
        if (extension is not (".dll" or ".so" or ".dylib"))
            return;
        // 同名程序集散落在多个插件目录意味着类型身份分裂风险;提示作者改用共享依赖声明。
        var simpleName = Path.GetFileNameWithoutExtension(file);
        if (seenAssemblies.TryGetValue(simpleName, out var firstDirectory)
            && !string.Equals(firstDirectory, containerDirectory, StringComparison.Ordinal))
        {
            skipped.Add(new PluginSkip(file,
                $"同名程序集也存在于 {firstDirectory};若它的类型会跨插件流动(服务契约/事件载荷),"
                + "请声明 [assembly: DshSharedDependency] 并移入 .shared,否则两个插件各持一份类型"));
        }
        else
        {
            seenAssemblies[simpleName] = containerDirectory;
        }
        if (extension == ".dll")
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                if (nativeBridge is not null && TryLoadNative(file, nativeBridge, packages, nativePlugins, skipped, flatLayout))
                    return;
                skipped.Add(new PluginSkip(file,
                    "NativeAOT 构建不能在运行期装载托管程序集;请把它编译进镜像,或改用原生插件"));
                return;
            }
            TryLoadManaged(file, packages, managed, skipped, flatLayout, out var handled);
            if (handled)
                return;
        }
        if (nativeBridge is not null)
            _ = TryLoadNative(file, nativeBridge, packages, nativePlugins, skipped, flatLayout);
    }

    private void TryLoadManaged(
        string file,
        List<string> packages,
        List<ManagedPlugin> managed,
        List<PluginSkip> skipped,
        bool flatLayout,
        out bool handled)
    {
        PluginLoadResult result;
        try
        {
            result = TryLoad(file);
        }
        catch (BadImageFormatException)
        {
            handled = false;   // 原生共享库(Windows 下同样是 .dll)交给原生装载
            return;
        }
        catch (Exception error)
        {
            skipped.Add(new PluginSkip(file, $"托管插件装载失败:{error.Message}"));
            handled = true;
            return;
        }
        skipped.AddRange(result.Skipped);
        handled = result.Packages.Count > 0 || result.Skipped.Count > 0;
        if (flatLayout && result.Packages.Count > 0)
        {
            skipped.Add(new PluginSkip(file,
                "平铺布局已废弃,将在下个版本停止扫描:请把插件及其依赖移入 plugins/<目录>/"));
        }
        foreach (var package in result.Packages)
        {
            managed.Add(new ManagedPlugin(package, result.Context!, result.SharedDependencies));
            packages.Add(package);
        }
    }

    private bool TryLoadNative(
        string file,
        Func<INativePlugin, IDshPlugin> bridge,
        List<string> packages,
        List<INativePlugin> nativePlugins,
        List<PluginSkip> skipped,
        bool flatLayout)
    {
        var plugin = NativePluginLibrary.TryLoad(file);
        if (plugin is null)
            return false;
        try
        {
            Catalog.Register(PluginDescriptor.For(plugin.Package, PluginForm.NativeLibrary), () => bridge(plugin));
        }
        catch (InvalidOperationException error)
        {
            plugin.Dispose();
            skipped.Add(new PluginSkip(file, error.Message));
            return true;
        }
        if (flatLayout)
        {
            skipped.Add(new PluginSkip(file,
                "平铺布局已废弃,将在下个版本停止扫描:请把插件及其依赖移入 plugins/<目录>/"));
        }
        nativePlugins.Add(plugin);
        packages.Add(plugin.Package);
        return true;
    }

    private static PluginDescriptor Descriptor(DshPluginRegistration registration, PluginForm form)
        => PluginDescriptor.For(registration.Package, form) with
        {
            Entry = registration.Entry,
            Version = registration.DescriptorVersion,
        };

    /** 宿主镜像(可执行文件旁)的程序集名集合:按磁盘事实判定,不按命名前缀。 */
    private static HashSet<string> HostImageNames()
        => new(
            Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ReadSharedDependencies(Assembly assembly)
        => assembly.GetType(ManifestTypeName)
            ?.GetMethod(SharedDependenciesMethodName, Type.EmptyTypes)
            ?.Invoke(null, null) as IReadOnlyList<string> ?? [];

    private static IReadOnlyList<DshPluginRegistration> ReadManifest(Assembly assembly, out string? failure)
    {
        failure = null;
        var method = assembly.GetType(ManifestTypeName)?.GetMethod(RegistrationsMethodName, Type.EmptyTypes);
        if (method is null)
            return [];
        try
        {
            return method.Invoke(null, null) as IReadOnlyList<DshPluginRegistration> ?? [];
        }
        catch (Exception error)
        {
            failure = $"插件清单读取失败:{error.GetBaseException().Message}";
            return [];
        }
    }
}
