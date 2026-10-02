using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Dsh.Plugins.Generator;

/** 从 IDshPlugin 实现与程序集声明生成插件清单:
 *  - 插件程序集产出 DshPluginManifest(托管 dll 形态的入口,宿主按类型名读取);
 *  - DshPluginHost=true 的宿主程序集产出聚合目录 DshPluginCatalog,并以模块初始化自注册。
 *  两者都用工厂直接构造插件实例,宿主不再反射遍历插件类型。 */
[Generator(LanguageNames.CSharp)]
public sealed partial class DshPluginCatalogGenerator : IIncrementalGenerator
{
    private const string InterfaceMetadataName = "Dsh.Plugins.IDshPlugin";
    private const string AttributeMetadataName = "Dsh.Plugins.DshPluginAttribute";
    private const string EntryAttributeMetadataName = "Dsh.Plugins.DshEntrypointAttribute";
    private const string InitializerAttributeMetadataName = "Dsh.Plugins.DshPluginInitializerAttribute";
    private const string SharedDependencyMetadataName = "Dsh.Plugins.DshSharedDependencyAttribute";
    private const string JsonContextMetadataName = "System.Text.Json.Serialization.JsonSerializerContext";
    private const string BootstrapTypeSuffix = ".Generated.DshPluginBootstrap";
    private const string HostPropertyName = "build_property.DshPluginHost";
    private const string ManifestHintName = "DshPluginManifest.g.cs";
    private const string BootstrapHintName = "DshPluginBootstrap.g.cs";
    private const string CatalogHintName = "DshPluginCatalog.g.cs";

    private static readonly DiagnosticDescriptor MultiplePluginTypes = new(
        "DSHPLUGIN001",
        "程序集包含多个 IDshPlugin 实现",
        "程序集 '{0}' 必须恰好包含一个 IDshPlugin 实现,实际找到 {1} 个",
        "DshPlugins",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingConstructor = new(
        "DSHPLUGIN002",
        "插件类型缺少可用构造函数",
        "插件类型 '{0}' 需要公共无参构造函数或公共 (string packageName) 构造函数",
        "DshPlugins",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidEntrypoint = new(
        "DSHPLUGIN003",
        "入口名无效",
        "插件类型 '{0}' 的 DshEntrypoint 名称必须是非空字符串",
        "DshPlugins",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidInitializer = new(
        "DSHPLUGIN004",
        "初始化方法签名无效",
        "方法 '{0}' 标了 DshPluginInitializer,但必须是返回 void 的无参静态方法",
        "DshPlugins",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingPackageName = new(
        "DSHPLUGIN005",
        "插件包名缺失",
        "程序集含 IDshPlugin 实现,但没有 [assembly: DshPlugin] 声明且程序集名不可用,无法确定包名",
        "DshPlugins",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var input = context.CompilationProvider.Combine(context.AnalyzerConfigOptionsProvider);
        context.RegisterSourceOutput(input, static (productionContext, pair) =>
            Execute(productionContext, pair.Left, pair.Right));
    }

    private static void Execute(
        SourceProductionContext context,
        Compilation compilation,
        AnalyzerConfigOptionsProvider options)
    {
        // 汇编级注册桥不依赖插件声明面:Dsh.Llm 这类只带 JSON 上下文的程序集也要生成。
        var bootstrap = DescribeBootstrap(context, compilation);
        if (bootstrap is not null)
            context.AddSource(BootstrapHintName, Templates.RenderBootstrap(NamespaceFor(compilation.AssemblyName ?? compilation.Assembly.Name), bootstrap));

        var pluginInterface = compilation.GetTypeByMetadataName(InterfaceMetadataName);
        var pluginAttribute = compilation.GetTypeByMetadataName(AttributeMetadataName);
        var entryAttribute = compilation.GetTypeByMetadataName(EntryAttributeMetadataName);
        var own = pluginInterface is null || pluginAttribute is null
            ? []
            : DescribeAssembly(context, compilation.Assembly, pluginInterface, pluginAttribute, entryAttribute);
        if (own.Count > 0)
            context.AddSource(ManifestHintName, Templates.RenderManifest(Deduplicate(own), DescribeSharedDependencies(compilation)));

        var isHost = options.GlobalOptions.TryGetValue(HostPropertyName, out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        if (!isHost)
            return;

        var registrations = new List<PluginRegistration>(own);
        var bootstraps = new List<string>();
        if (bootstrap is not null)
            bootstraps.Add($"global::{NamespaceFor(compilation.AssemblyName ?? compilation.Assembly.Name)}{BootstrapTypeSuffix}");
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly))
                continue;
            if (pluginInterface is not null && pluginAttribute is not null)
                registrations.AddRange(DescribeAssembly(context, assembly, pluginInterface, pluginAttribute, entryAttribute));
            if (assembly.GetTypeByMetadataName($"{NamespaceFor(assembly.Name)}{BootstrapTypeSuffix}") is { } referenced)
                bootstraps.Add(referenced.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }
        if (registrations.Count == 0 && bootstraps.Count == 0)
            return;
        context.AddSource(CatalogHintName, Templates.RenderCatalog(Deduplicate(registrations), bootstraps));
    }

    /** 汇编级显式注册:源生成 JSON 上下文 + 标了 DshPluginInitializer 的静态方法(字典序保证确定性)。 */
    private static BootstrapSpec? DescribeBootstrap(SourceProductionContext context, Compilation compilation)
    {
        var initializerAttribute = compilation.GetTypeByMetadataName(InitializerAttributeMetadataName);
        var initializers = new List<string>();
        if (initializerAttribute is not null)
        {
            foreach (var method in EnumerateTypes(compilation.Assembly.GlobalNamespace)
                .SelectMany(type => type.GetMembers().OfType<IMethodSymbol>()))
            {
                if (!method.GetAttributes().Any(attribute => attribute.AttributeClass is not null
                    && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, initializerAttribute)))
                {
                    continue;
                }
                if (!method.IsStatic || method.Parameters.Length != 0 || method.ReturnsVoid is false)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        InvalidInitializer, LocationOf(method), method.ToDisplayString()));
                    continue;
                }
                initializers.Add(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    + "." + method.Name);
            }
        }
        var jsonContexts = FindJsonContexts(compilation);
        if (initializers.Count == 0 && jsonContexts.Count == 0)
            return null;
        initializers.Sort(StringComparer.Ordinal);
        return new BootstrapSpec(initializers, jsonContexts);
    }

    /** 本程序集的源生成 JSON 上下文:宿主静态引用后,裁剪/AOT 下上下文与载荷元数据一并保留。 */
    private static List<string> FindJsonContexts(Compilation compilation)
    {
        var baseType = compilation.GetTypeByMetadataName(JsonContextMetadataName);
        if (baseType is null)
            return [];
        return EnumerateTypes(compilation.Assembly.GlobalNamespace)
            .Where(type => type.TypeKind == TypeKind.Class && !type.IsAbstract && DerivesFrom(type, baseType))
            .Select(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /** 程序集声明的共享依赖(DshSharedDependency):清单携带,加载器据此把这些程序集解析进共享上下文。 */
    private static List<string> DescribeSharedDependencies(Compilation compilation)
    {
        var sharedAttribute = compilation.GetTypeByMetadataName(SharedDependencyMetadataName);
        if (sharedAttribute is null)
            return [];
        return compilation.Assembly.GetAttributes()
            .Where(attribute => attribute.AttributeClass is not null
                && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, sharedAttribute))
            .Select(attribute => attribute.ConstructorArguments.FirstOrDefault().Value as string)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
        }

        return false;
    }

    /** 桥类型按程序集命名,避免多个插件程序集的同名公开类型在宿主侧歧义。 */
    private static string NamespaceFor(string assemblyName)
    {
        var builder = new System.Text.StringBuilder(assemblyName.Length);
        foreach (var ch in assemblyName)
            builder.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' ? ch : '_');
        if (builder.Length == 0 || char.IsDigit(builder[0]))
            builder.Insert(0, '_');
        return builder.ToString();
    }

    private static List<PluginRegistration> Deduplicate(IEnumerable<PluginRegistration> registrations)
        => registrations
            .GroupBy(registration => (registration.Package, registration.TypeName))
            .Select(group => group.First())
            .OrderBy(registration => registration.Package, StringComparer.Ordinal)
            .ThenBy(registration => registration.TypeName, StringComparer.Ordinal)
            .ToList();

    private static List<PluginRegistration> DescribeAssembly(
        SourceProductionContext context,
        IAssemblySymbol assembly,
        INamedTypeSymbol pluginInterface,
        INamedTypeSymbol pluginAttribute,
        INamedTypeSymbol? entryAttribute)
    {
        var pluginTypes = EnumerateTypes(assembly.GlobalNamespace)
            .Where(type => type.TypeKind == TypeKind.Class && !type.IsAbstract && !type.IsStatic && ImplementsPlugin(type, pluginInterface))
            .ToList();
        if (pluginTypes.Count == 0)
            return [];

        var packages = assembly.GetAttributes()
            .Where(attribute => attribute.AttributeClass is not null
                && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, pluginAttribute))
            .Select(attribute => attribute.ConstructorArguments.FirstOrDefault().Value as string)
            .Where(package => !string.IsNullOrEmpty(package))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        // [assembly: DshPlugin] 只是包名的覆盖声明;缺省从程序集名推导,只有连程序集名都不可得才报。
        if (packages.Count == 0)
        {
            if (string.IsNullOrEmpty(assembly.Name))
            {
                context.ReportDiagnostic(Diagnostic.Create(MissingPackageName, Location.None));
                return [];
            }
            packages = [assembly.Name];
        }

        if (pluginTypes.Count != 1)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MultiplePluginTypes, Location.None, assembly.Name, pluginTypes.Count));
            return [];
        }

        var pluginType = pluginTypes[0];
        var hasPackageConstructor = pluginType.InstanceConstructors.Any(ctor =>
            ctor.DeclaredAccessibility == Accessibility.Public
            && ctor.Parameters.Length == 1
            && ctor.Parameters[0].Type.SpecialType == SpecialType.System_String);
        var hasDefaultConstructor = pluginType.InstanceConstructors.Any(ctor =>
            ctor.DeclaredAccessibility == Accessibility.Public && ctor.Parameters.Length == 0);
        if (!hasPackageConstructor && !hasDefaultConstructor)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingConstructor, LocationOf(pluginType), pluginType.ToDisplayString()));
            return [];
        }

        var entry = ResolveEntry(context, pluginType, entryAttribute);
        var typeName = pluginType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var registrations = new List<PluginRegistration>(packages.Count);
        foreach (var package in packages)
            registrations.Add(new PluginRegistration(package!, typeName, hasPackageConstructor, entry));
        return registrations;
    }

    private static string? ResolveEntry(
        SourceProductionContext context,
        INamedTypeSymbol pluginType,
        INamedTypeSymbol? entryAttribute)
    {
        if (entryAttribute is null)
            return null;
        var attribute = pluginType.GetAttributes().FirstOrDefault(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, entryAttribute));
        if (attribute is null)
            return null;
        var name = attribute.ConstructorArguments.FirstOrDefault().Value as string;
        if (string.IsNullOrEmpty(name))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidEntrypoint, LocationOf(attribute, pluginType), pluginType.ToDisplayString()));
            return null;
        }
        return name;
    }

    private static Location LocationOf(ISymbol symbol)
        => symbol.Locations.FirstOrDefault(location => location.IsInSource) ?? Location.None;

    private static Location LocationOf(AttributeData attribute, ISymbol fallback)
        => attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? LocationOf(fallback);

    private static bool ImplementsPlugin(INamedTypeSymbol type, INamedTypeSymbol pluginInterface)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.AllInterfaces.Any(interfaceType => SymbolEqualityComparer.Default.Equals(interfaceType, pluginInterface)))
                return true;
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
            yield return type;
        foreach (var child in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in EnumerateTypes(child))
                yield return type;
        }
    }

    /** 汇编级注册清单:初始化方法 + 源生成 JSON 上下文。 */
    private sealed class BootstrapSpec
    {
        public BootstrapSpec(List<string> initializers, List<string> jsonContexts)
        {
            Initializers = initializers;
            JsonContexts = jsonContexts;
        }

        public List<string> Initializers { get; }

        public List<string> JsonContexts { get; }
    }

    /** 一个 (包名 → 插件类型) 登记项:工厂在生成代码里直接 new,不再经过反射。 */
    private sealed class PluginRegistration
    {
        public PluginRegistration(string package, string typeName, bool needsPackage, string? entry)
        {
            Package = package;
            TypeName = typeName;
            NeedsPackage = needsPackage;
            Entry = entry;
        }

        public string Package { get; }

        public string TypeName { get; }

        public bool NeedsPackage { get; }

        public string? Entry { get; }
    }
}
