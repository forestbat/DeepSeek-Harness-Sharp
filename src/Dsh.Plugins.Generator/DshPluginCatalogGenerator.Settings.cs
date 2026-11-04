using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Dsh.Plugins.Generator;

/**
 * 配置面生成: 插件的参数面来自两类声明——
 *  - `IDshPlugin.ConfigType => typeof(X)`: 与宿主绑定同一个类型(声明即启用未知键告警);
 *  - 标了 [DshPluginSettings] 的类型: 只声明文档面, 不改变运行期解析(适合自己解析参数的插件, 可多类型合并)。
 * 生成器读这些类型的**语法**(属性名/类型/初始化器默认值/嵌套记录)产出 ISettingsSchemaProvider,
 * 并在模块初始化器里注册。只读语法不反射: 默认值以字面量落进生成代码, 裁剪/AOT 下与 JIT 行为一致。
 * 顶层 settings 文档由标了 [DshSettingsDocument] 的类型(如 HarnessSettings)产出, IsPlugin=false。
 */
public sealed partial class DshPluginCatalogGenerator
{
    /** 与 BootstrapTypeSuffix 同款的按名约定: 宿主与文档工具据此找到插件程序集里的配置面。 */
    public const string SchemaTypeSuffix = ".Generated.DshPluginSettingsSchema";

    private const string SchemaHintName = "DshPluginSettingsSchema.g.cs";
    private const string SettingsAttributeMetadataName = "Dsh.Plugins.DshPluginSettingsAttribute";
    private const string DocumentAttributeMetadataName = "Dsh.Plugins.DshSettingsDocumentAttribute";

    private static void EmitSettingsSchema(SourceProductionContext context, Compilation compilation)
    {
        if (compilation.AssemblyName is null)
            return;
        var namespaceName = NamespaceFor(compilation.AssemblyName);
        if (FindDocumentType(compilation) is { } document)
        {
            context.AddSource(SchemaHintName,
                RenderSchemaSource(namespaceName, "dsh", isPlugin: false, DescribeSettings(document, compilation, depth: 0)));
            return;
        }

        var pluginInterface = compilation.GetTypeByMetadataName(InterfaceMetadataName);
        if (pluginInterface is null)
            return;
        var pluginTypes = EnumerateTypes(compilation.Assembly.GlobalNamespace)
            .Where(type => type.TypeKind == TypeKind.Class && !type.IsAbstract && !type.IsStatic && ImplementsPlugin(type, pluginInterface))
            .ToList();
        if (pluginTypes.Count != 1)
            return;
        var package = ResolvePackage(compilation) ?? compilation.AssemblyName;
        var settings = new List<SchemaEntry>();
        foreach (var type in ConfigTypesOf(compilation, pluginTypes[0]))
            Merge(settings, DescribeSettings(type, compilation, depth: 0));
        if (settings.Count == 0)
            return;
        context.AddSource(SchemaHintName, RenderSchemaSource(namespaceName, package, isPlugin: true, settings));
    }

    /** 参数面来源: ConfigType 声明的类型 + 本程序集里标了 [DshPluginSettings] 的类型(去重保序)。 */
    private static List<INamedTypeSymbol> ConfigTypesOf(Compilation compilation, INamedTypeSymbol pluginType)
    {
        var types = new List<INamedTypeSymbol>();
        if (ResolveConfigType(compilation, pluginType) is { } declared)
            types.Add(declared);
        var attribute = compilation.GetTypeByMetadataName(SettingsAttributeMetadataName);
        if (attribute is not null)
        {
            foreach (var type in EnumerateTypes(compilation.Assembly.GlobalNamespace))
            {
                if (type.GetAttributes().Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute))
                    && !types.Any(existing => SymbolEqualityComparer.Default.Equals(existing, type)))
                {
                    types.Add(type);
                }
            }
        }
        return types;
    }

    private static INamedTypeSymbol? FindDocumentType(Compilation compilation)
    {
        var attribute = compilation.GetTypeByMetadataName(DocumentAttributeMetadataName);
        if (attribute is null)
            return null;
        return EnumerateTypes(compilation.Assembly.GlobalNamespace)
            .FirstOrDefault(type => type.GetAttributes().Any(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute)));
    }

    /** 同名键以先声明者为准(ConfigType 在标注类型之前), 保证合并结果稳定。 */
    private static void Merge(List<SchemaEntry> target, List<SchemaEntry> source)
    {
        foreach (var entry in source)
        {
            if (target.Any(existing => string.Equals(existing.Key, entry.Key, System.StringComparison.Ordinal)))
                continue;
            target.Add(entry);
        }
    }

    /** ConfigType 属性声明的绑定类型: 只认 `public Type ConfigType => typeof(X);` 这种直接形式(与宿主绑定语义一致)。 */
    private static INamedTypeSymbol? ResolveConfigType(Compilation compilation, INamedTypeSymbol pluginType)
    {
        var property = pluginType.GetMembers("ConfigType").OfType<IPropertySymbol>().FirstOrDefault();
        var syntax = property?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax;
        if (syntax?.ExpressionBody?.Expression is not TypeOfExpressionSyntax typeOf)
            return null;
        var model = compilation.GetSemanticModel(typeOf.SyntaxTree);
        return model.GetTypeInfo(typeOf.Type).Type as INamedTypeSymbol;
    }

    /** 包名: 取 [assembly: DshPlugin] 声明的第一个(与注册表解析规则一致)。 */
    private static string? ResolvePackage(Compilation compilation)
    {
        var attribute = compilation.GetTypeByMetadataName(AttributeMetadataName);
        if (attribute is null)
            return null;
        return compilation.Assembly.GetAttributes()
            .Where(candidate => candidate.AttributeClass is not null
                && SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute))
            .Select(candidate => candidate.ConstructorArguments.FirstOrDefault().Value as string)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
    }

    /** 绑定类型的可写属性 → 配置项(嵌套类型递归); 深度上限防止自引用类型死循环。 */
    private static List<SchemaEntry> DescribeSettings(INamedTypeSymbol type, Compilation compilation, int depth)
    {
        var entries = new List<SchemaEntry>();
        if (depth > 4)
            return entries;
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.SetMethod is null || property.IsIndexer)
                continue;
            if (property.DeclaredAccessibility != Accessibility.Public)
                continue;
            var kind = KindOf(property.Type);
            var children = kind == SchemaKind.Mapping && ChildTypeOf(property.Type) is { } nested
                ? DescribeSettings(nested, compilation, depth + 1)
                : [];
            entries.Add(new SchemaEntry(
                KeyOf(property),
                kind,
                DefaultLiteralOf(property),
                children.Count == 0 ? null : children)
            {
                Description = DescriptionOf(property),
            });
        }
        return entries;
    }

    /** 只使用声明处的 XML 注释。*/
    private static string? DescriptionOf(IPropertySymbol property)
        => SummaryOf(property) is { Length: > 0 } summary ? summary : null;

    /**
     * 读声明处的注释摘要; 两种仓库风格都认(`/// <summary>` 与 `/** … *&#47;`), 无注释返回空。
     * 取首个非空行, 去掉注释符号与行首 `*`。
     */
    private static string? SummaryOf(IPropertySymbol property)
    {
        if (property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not PropertyDeclarationSyntax syntax)
            return null;
        foreach (var trivia in syntax.GetLeadingTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
                && !trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                continue;
            }
            foreach (var raw in trivia.ToString().Split('\n'))
            {
                var line = StripCommentMarkers(raw);
                if (line.StartsWith("<", StringComparison.Ordinal) || line.StartsWith("</", StringComparison.Ordinal))
                    continue;
                line = line.Replace("*/", "").Trim();
                if (line.Length == 0)
                    continue;
                var stop = line.IndexOf("。", StringComparison.Ordinal);
                return stop > 0 ? line.Substring(0, stop) : line;
            }
        }
        return null;
    }

    /** 去掉行首的注释符号(`///`, `/**`, `*`)与空白。 */
    private static string StripCommentMarkers(string raw)
    {
        var line = raw.Trim();
        while (line.Length > 0 && (line[0] == '/' || line[0] == '*'))
            line = line.Substring(1).Trim();
        return line;
    }

    /** 键名: [DshPluginSettings(Name)] > [YamlMember(Alias)] > 属性名 camelCase(与 PluginConfigBinding 的忽略大小写匹配一致)。 */
    private static string KeyOf(IPropertySymbol property)
    {
        if (PropertyAttributeName(property) is { Length: > 0 } declared)
            return declared;
        if (YamlAliasOf(property) is { Length: > 0 } alias)
            return alias;
        var name = property.Name;
        return name.Length == 0 ? name : $"{char.ToLowerInvariant(name[0])}{name.Substring(1)}";
    }

    private static string? YamlAliasOf(IPropertySymbol property)
    {
        foreach (var attribute in property.GetAttributes())
        {
            if (attribute.AttributeClass?.Name != "YamlMemberAttribute")
                continue;
            if (attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "Alias").Value.Value is string alias)
                return alias;
            if (attribute.ConstructorArguments.FirstOrDefault().Value is string positional)
                return positional;
        }
        return null;
    }

    private static string? PropertyAttributeName(IPropertySymbol property)
    {
        foreach (var attribute in property.GetAttributes())
        {
            if (attribute.AttributeClass?.Name != "DshPluginSettingsAttribute")
                continue;
            if (attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "Name").Value.Value is string named)
                return named;
        }
        return null;
    }

    private static SchemaKind KindOf(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } nullable)
            return KindOf(nullable.TypeArguments[0]);
        if (type.SpecialType == SpecialType.System_Boolean)
            return SchemaKind.Boolean;
        if (type.SpecialType is SpecialType.System_String or SpecialType.System_Char)
            return SchemaKind.Text;
        if (type.TypeKind == TypeKind.Enum)
            return SchemaKind.Text;
        if (type.SpecialType is SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64
            or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64
            or SpecialType.System_Byte or SpecialType.System_SByte)
        {
            return SchemaKind.Integer;
        }
        if (type.SpecialType is SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal
            or SpecialType.System_DateTime)
        {
            return SchemaKind.Number;
        }
        if (type is IArrayTypeSymbol || IsDictionary(type))
            return SchemaKind.Mapping;
        if (IsEnumerable(type))
            return SchemaKind.List;
        // 普通类/结构体只要带公开可写属性就当作嵌套配置段(文档面如 subagent/logging/storage 不是 record)。
        return type is INamedTypeSymbol named && HasWritableProperties(named) ? SchemaKind.Mapping : SchemaKind.Unknown;
    }

    private static bool HasWritableProperties(INamedTypeSymbol type)
        => type.GetMembers().OfType<IPropertySymbol>().Any(property =>
            property.SetMethod is not null && property.DeclaredAccessibility == Accessibility.Public && !property.IsStatic);

    /** 嵌套子项: 映射取"值类型"的属性(字典本身只有 Comparer/Count 这类成员, 列出来是噪声)。 */
    private static INamedTypeSymbol? ChildTypeOf(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return null;
        if (IsDictionary(named) && named.TypeArguments.Length == 2 && named.TypeArguments[1] is INamedTypeSymbol value)
            return value;
        return named;
    }

    private static bool IsDictionary(ITypeSymbol type)
    {
        foreach (var candidate in Candidates(type))
        {
            var definition = candidate.OriginalDefinition.ToDisplayString();
            if (definition is "System.Collections.Generic.IDictionary<TKey, TValue>"
                or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                or "System.Collections.Generic.Dictionary<TKey, TValue>")
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> Candidates(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            yield break;
        yield return named;
        foreach (var candidate in named.AllInterfaces)
            yield return candidate;
    }

    private static bool IsEnumerable(ITypeSymbol type)
        => type is INamedTypeSymbol named
            && named.AllInterfaces.Any(candidate => candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            && named.SpecialType != SpecialType.System_String;

    /** 默认值: 取属性的初始化器文本(`= true` / `= 30` / `= []`); 主构造参数默认值与无初始化器都留空。 */
    private static string? DefaultLiteralOf(IPropertySymbol property)
    {
        if (property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not PropertyDeclarationSyntax syntax)
            return null;
        return syntax.Initializer?.Value.ToString();
    }

    /** 生成 ISettingsSchemaProvider 实现: 键名/类型/默认值全部是字面量, 运行期不做反射。 */
    private static SourceText RenderSchemaSource(string namespaceName, string package, bool isPlugin, List<SchemaEntry> settings)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine($"namespace {namespaceName}.Generated");
        builder.AppendLine("{");
        builder.AppendLine("    public sealed class DshPluginSettingsSchema : global::Dsh.Plugins.ISettingsSchemaProvider");
        builder.AppendLine("    {");
        builder.AppendLine($"        public string Package => {Literal(package)};");
        if (!isPlugin)
            builder.AppendLine("        public bool IsPlugin => false;");
        builder.AppendLine();
        builder.AppendLine("        public global::System.Collections.Generic.IReadOnlyList<global::Dsh.Plugins.PluginSettingSpec> Settings { get; } =");
        builder.AppendLine("        [");
        foreach (var entry in settings)
            builder.AppendLine($"            {SpecSource(entry)},");
        builder.AppendLine("        ];");
        builder.AppendLine();
        builder.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        builder.AppendLine("        internal static void Register()");
        builder.AppendLine("            => global::Dsh.Plugins.DshPluginSettingsRegistry.Register(new DshPluginSettingsSchema());");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return SourceText.From(builder.ToString(), Encoding.UTF8);
    }

    private static string SpecSource(SchemaEntry entry)
    {
        var kind = $"global::Dsh.Plugins.PluginSettingKind.{entry.Kind}";
        if (entry.Children is null || entry.Children.Count == 0)
            return $"new({Literal(entry.Key)}, {kind}, {Literal(entry.DefaultLiteral)}, {Literal(entry.Description)})";
        var children = string.Join(", ", entry.Children.Select(SpecSource));
        return $"new({Literal(entry.Key)}, {kind}, {Literal(entry.DefaultLiteral)}, {Literal(entry.Description)}, [{children}])";
    }

    private static string Literal(string? value)
        => value is null ? "null" : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/** 生成期用的配置项(与运行期 PluginSettingSpec 对齐, 生成代码里逐字段落字面量)。 */
internal sealed class SchemaEntry(string key, SchemaKind kind, string? defaultLiteral, List<SchemaEntry>? children)
{
    public string Key { get; } = key;

    public SchemaKind Kind { get; } = kind;

    public string? DefaultLiteral { get; } = defaultLiteral;

    public List<SchemaEntry>? Children { get; } = children;

    /** 声明处 /// 摘要, 或按名字/类型派生的最小提示。 */
    public string? Description { get; set; }
}

internal enum SchemaKind
{
    Text,
    Boolean,
    Integer,
    Number,
    List,
    Mapping,
    Unknown,
}
