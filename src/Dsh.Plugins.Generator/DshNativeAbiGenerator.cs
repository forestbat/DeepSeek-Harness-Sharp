using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Dsh.Plugins.Generator;

/** 从协议声明(带角色的函数表字段)生成原生 ABI 两侧胶水:
 *  - 实现 IDshNativePlugin 的插件程序集:导出 dsh_plugin_package/dsh_plugin_entry 与工具调度,含缓冲协议与异常拦截;
 *  - DshNativeHost=true 的宿主程序集:宿主函数表、回调蹦床与插件函数表绑定(握手 + 两段式调用)。
 *  协议结构体、常量与生命周期框架仍是手写边界。 */
[Generator(LanguageNames.CSharp)]
public sealed partial class DshNativeAbiGenerator : IIncrementalGenerator
{
    private const string NativePluginInterfaceMetadataName = "Dsh.Plugins.Native.IDshNativePlugin";
    private const string AbiConstantsMetadataName = "Dsh.Plugins.Native.DshNativePluginAbi";
    private const string HostApiMetadataName = "Dsh.Plugins.Native.DshHostApi";
    private const string PluginApiMetadataName = "Dsh.Plugins.Native.DshPluginApi";
    private const string RoleAttributeMetadataName = "Dsh.Plugins.Native.DshAbiRoleAttribute";
    private const string NativeHostPropertyName = "build_property.DshNativeHost";
    private const string PluginExportsHintName = "DshNativePluginExports.g.cs";
    private const string HostApiHintName = "DshNativeHostApi.g.cs";

    private static readonly DiagnosticDescriptor MultipleNativePlugins = new(
        "DSHABI001",
        "程序集包含多个 IDshNativePlugin 实现",
        "程序集 '{0}' 必须恰好包含一个 IDshNativePlugin 实现,实际找到 {1} 个",
        "DshPlugins",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingRole = new(
        "DSHABI002",
        "ABI 协议缺少角色",
        "字段表 '{0}' 缺少角色 '{1}',无法生成胶水",
        "DshPlugins",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnknownRole = new(
        "DSHABI003",
        "生成器不认识该 ABI 角色",
        "字段 '{0}' 的角色 '{1}' 没有对应的胶水模板,该函数不会进入函数表",
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
        var constants = compilation.GetTypeByMetadataName(AbiConstantsMetadataName);
        var hostApiType = compilation.GetTypeByMetadataName(HostApiMetadataName);
        var pluginApiType = compilation.GetTypeByMetadataName(PluginApiMetadataName);
        if (constants is null || hostApiType is null || pluginApiType is null)
            return;
        var spec = ReadSpec(context, constants, hostApiType, pluginApiType);
        if (spec is null)
            return;
        ReportUnknownRoles(context, hostApiType, pluginApiType, constants, [
            "log", "register_tool", "unregister_tool", "package", "activate", "deactivate", "invoke_tool",
        ]);

        var pluginInterface = compilation.GetTypeByMetadataName(NativePluginInterfaceMetadataName);
        if (pluginInterface is not null)
        {
            var implementations = EnumerateTypes(compilation.Assembly.GlobalNamespace)
                .Where(type => type.TypeKind == TypeKind.Class && !type.IsAbstract && !type.IsStatic && Implements(type, pluginInterface))
                .ToList();
            if (implementations.Count > 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MultipleNativePlugins, Location.None, compilation.Assembly.Name, implementations.Count));
            }
            else if (implementations.Count == 1)
            {
                context.AddSource(PluginExportsHintName, Templates.RenderNativePluginExports(
                    implementations[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), spec));
            }
        }

        var isNativeHost = options.GlobalOptions.TryGetValue(NativeHostPropertyName, out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        if (isNativeHost)
            context.AddSource(HostApiHintName, Templates.RenderNativeHostApi(spec));
    }

    private static AbiSpec? ReadSpec(
        SourceProductionContext context,
        INamedTypeSymbol constants,
        INamedTypeSymbol hostApiType,
        INamedTypeSymbol pluginApiType)
    {
        var roleAttribute = constants.ContainingAssembly.GetTypeByMetadataName(RoleAttributeMetadataName);
        if (roleAttribute is null)
            return null;
        var spec = new AbiSpec
        {
            Version = Constant(constants, "Version", 0),
            EntryExport = Constant(constants, "EntryPoint", ""),
            PackageExport = Constant(constants, "PackageExport", ""),
            VersionExport = Constant(constants, "VersionExport", ""),
            Ok = Constant(constants, "Ok", 0),
            Error = Constant(constants, "Error", -1),
            LogInfo = Constant(constants, "LogInfo", 1),
            LogError = Constant(constants, "LogError", 3),
            PluginPackageField = Field(pluginApiType, roleAttribute, "package"),
            PluginActivateField = Field(pluginApiType, roleAttribute, "activate"),
            PluginDeactivateField = Field(pluginApiType, roleAttribute, "deactivate"),
            PluginInvokeToolField = Field(pluginApiType, roleAttribute, "invoke_tool"),
            HostLogField = Field(hostApiType, roleAttribute, "log"),
            HostRegisterToolField = Field(hostApiType, roleAttribute, "register_tool"),
            HostUnregisterToolField = Field(hostApiType, roleAttribute, "unregister_tool"),
        };
        foreach (var (type, role, field) in new[]
        {
            (pluginApiType.Name, "package", spec.PluginPackageField),
            (pluginApiType.Name, "activate", spec.PluginActivateField),
            (pluginApiType.Name, "deactivate", spec.PluginDeactivateField),
            (pluginApiType.Name, "invoke_tool", spec.PluginInvokeToolField),
            (hostApiType.Name, "log", spec.HostLogField),
            (hostApiType.Name, "register_tool", spec.HostRegisterToolField),
            (hostApiType.Name, "unregister_tool", spec.HostUnregisterToolField),
        })
        {
            if (field.Length == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(MissingRole, Location.None, type, role));
                return null;
            }
        }
        return spec;
    }

    private static string Field(INamedTypeSymbol type, INamedTypeSymbol roleAttribute, string role)
        => type.GetMembers().OfType<IFieldSymbol>()
            .FirstOrDefault(field => field.GetAttributes().Any(attribute =>
                attribute.AttributeClass is not null
                && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, roleAttribute)
                && attribute.ConstructorArguments.FirstOrDefault().Value as string == role))
            ?.Name ?? "";

    /** 协议加了新角色但没有对应胶水模板时给出警告,避免函数表字段被静默漏掉。 */
    private static void ReportUnknownRoles(
        SourceProductionContext context,
        INamedTypeSymbol hostApiType,
        INamedTypeSymbol pluginApiType,
        INamedTypeSymbol constants,
        IReadOnlyList<string> knownRoles)
    {
        var roleAttribute = constants.ContainingAssembly.GetTypeByMetadataName(RoleAttributeMetadataName);
        if (roleAttribute is null)
            return;
        foreach (var type in new[] { hostApiType, pluginApiType })
        {
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                var role = field.GetAttributes()
                    .Where(attribute => attribute.AttributeClass is not null
                        && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, roleAttribute))
                    .Select(attribute => attribute.ConstructorArguments.FirstOrDefault().Value as string)
                    .FirstOrDefault();
                if (role is not null && !knownRoles.Contains(role))
                    context.ReportDiagnostic(Diagnostic.Create(UnknownRole, LocationOf(field), field.Name, role));
            }
        }
    }

    private static Location LocationOf(IFieldSymbol field)
        => field.Locations.FirstOrDefault(location => location.IsInSource) ?? Location.None;

    private static int Constant(INamedTypeSymbol type, string name, int fallback)
        => type.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault()?.ConstantValue is int value ? value : fallback;

    private static string Constant(INamedTypeSymbol type, string name, string fallback)
        => type.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault()?.ConstantValue as string ?? fallback;

    private static bool Implements(INamedTypeSymbol type, INamedTypeSymbol interfaceType)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, interfaceType)))
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

    /** ABI v1 协议在生成器侧的投影:常量值 + 各角色对应字段名。 */
    private sealed class AbiSpec
    {
        public int Version { get; set; }

        public string EntryExport { get; set; } = "";

        public string PackageExport { get; set; } = "";

        public string VersionExport { get; set; } = "";

        public int Ok { get; set; }

        public int Error { get; set; }

        public int LogInfo { get; set; }

        public int LogError { get; set; }

        public string PluginPackageField { get; set; } = "";

        public string PluginActivateField { get; set; } = "";

        public string PluginDeactivateField { get; set; } = "";

        public string PluginInvokeToolField { get; set; } = "";

        public string HostLogField { get; set; } = "";

        public string HostRegisterToolField { get; set; } = "";

        public string HostUnregisterToolField { get; set; } = "";
    }
}
