using System.Text;
using Dsh.Plugins;

namespace Dsh.Boot;

/**
 * settings 配置面的单一出口: 数据来自各程序集生成器登记的 ISettingsSchemaProvider(零反射)。
 * 顶层文档(IsPlugin=false)与插件参数一起, 供 dsharp config schema 与 /inspect config 共用。
 */
public static class SettingsSchemaCatalog
{
    public const string GeneratedBegin = "# >>> generated: settings-schema (配置项由 dsharp config schema --write-template 生成;键上方的说明注释会保留)";

    public const string GeneratedEnd = "# <<< generated";

    public static IReadOnlyList<ISettingsSchemaProvider> All() => DshPluginSettingsRegistry.All();

    public static ISettingsSchemaProvider? Document()
        => All().FirstOrDefault(provider => !provider.IsPlugin);

    public static IReadOnlyList<ISettingsSchemaProvider> Plugins()
        => [.. All().Where(provider => provider.IsPlugin)];

    /** 模板生成块: 键上方的注释由 preserved 覆盖 schema 描述。 */
    public static string RenderTemplateBlock(IReadOnlyDictionary<string, string>? preserved = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine(GeneratedBegin);
        if (Document() is { } document)
        {
            builder.AppendLine("# 顶层 settings(默认值来自 HarnessSettings, 取消注释即可覆盖):");
            RenderSpecs(document.Settings, "", 0, preserved, builder);
        }
        var plugins = Plugins();
        if (plugins.Count > 0)
        {
            builder.AppendLine("#");
            builder.AppendLine("# 插件参数(plugins.\"<包名>\": {enabled, permissions, ...参数}; 未列出的插件发现即启用):");
        }
        foreach (var plugin in plugins)
        {
            if (preserved?.GetValueOrDefault(plugin.Package) is { Length: > 0 } header)
                foreach (var line in header.Split('\n'))
                    if (line.Trim() is { Length: > 0 } text)
                        builder.Append("# ").AppendLine(text);
            builder.AppendLine($"# {plugin.Package}:");
            RenderSpecs(plugin.Settings, plugin.Package, 2, preserved, builder);
        }
        builder.Append(GeneratedEnd);
        return builder.ToString();
    }

    private static void RenderSpecs(IReadOnlyList<PluginSettingSpec> settings, string prefix, int indent, IReadOnlyDictionary<string, string>? preserved, StringBuilder builder)
    {
        var pad = new string(' ', indent);
        foreach (var spec in settings)
        {
            var path = prefix.Length == 0 ? spec.Key : $"{prefix}.{spec.Key}";
            var comment = preserved?.GetValueOrDefault(path) ?? spec.Description;
            if (!string.IsNullOrWhiteSpace(comment))
                foreach (var line in comment.Split('\n'))
                    if (line.Trim() is { Length: > 0 } text)
                        builder.Append("# ").Append(pad).AppendLine(text);
            builder.Append("# ").Append(pad).Append(spec.Key).Append(':');
            if (spec.Kind is not (PluginSettingKind.List or PluginSettingKind.Mapping)
                && spec.DefaultLiteral is { Length: > 0 } literal && IsLiteral(literal))
                builder.Append(' ').Append(literal.Replace("\n", " ").Replace("\r", ""));
            else if (spec.Kind == PluginSettingKind.List)
                builder.Append(" []");
            else if (spec.Kind == PluginSettingKind.Mapping && spec.Children is not { Count: > 0 })
                builder.Append(" {}");
            builder.AppendLine();
            if (spec.Children is { Count: > 0 } children)
                RenderSpecs(children, path, indent + 2, preserved, builder);
        }
    }

    private static bool IsLiteral(string literal)
    {
        var text = literal.Trim();
        if (text is "true" or "false" or "null" or "[]" or "{}")
            return true;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            return true;
        return text.Length > 1 && text[0] == '"' && text[^1] == '"';
    }

    /** 文本形态输出(CLI/文档共用): json 为结构化清单, markdown 为表格。 */
    public static string Render(string format)
        => format == "json" ? RenderJson() : RenderMarkdown();

    private static string RenderJson()
    {
        var builder = new StringBuilder();
        builder.AppendLine("[");
        var providers = All();
        for (var index = 0; index < providers.Count; index++)
        {
            var provider = providers[index];
            builder.Append($"  {{\"package\":{Quote(provider.Package)},\"plugin\":{(provider.IsPlugin ? "true" : "false")},\"settings\":[");
            builder.Append(string.Join(",", provider.Settings.Select(SettingsJson)));
            builder.Append(']').AppendLine(index == providers.Count - 1 ? "}" : "},");
        }
        return builder.AppendLine("]").ToString();
    }

    private static string RenderMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| 作用域 | 键 | 类型 | 默认值 |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var provider in All())
            AppendMarkdown(provider, provider.Settings, "", builder);
        return builder.ToString();
    }

    private static void AppendMarkdown(ISettingsSchemaProvider provider, IReadOnlyList<PluginSettingSpec> settings, string prefix, StringBuilder builder)
    {
        foreach (var spec in settings)
        {
            builder.Append("| ").Append(provider.Package).Append(" | ").Append(prefix).Append(spec.Key)
                .Append(" | ").Append(spec.Kind)
                .Append(" | ").Append(spec.DefaultLiteral ?? "").AppendLine(" |");
            if (spec.Children is { Count: > 0 } children)
                AppendMarkdown(provider, children, prefix + spec.Key + ".", builder);
        }
    }

    private static string SettingsJson(PluginSettingSpec spec)
    {
        var builder = new StringBuilder();
        builder.Append($"{{\"key\":{Quote(spec.Key)},\"kind\":\"{spec.Kind}\"");
        if (spec.DefaultLiteral is not null)
            builder.Append($",\"default\":{Quote(spec.DefaultLiteral)}");
        if (spec.Description is not null)
            builder.Append($",\"description\":{Quote(spec.Description)}");
        if (spec.Children is { Count: > 0 } children)
            builder.Append($",\"children\":[{string.Join(",", children.Select(SettingsJson))}]");
        return builder.Append('}').ToString();
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
