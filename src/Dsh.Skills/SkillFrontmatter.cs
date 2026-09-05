using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dsh.Skills;

public sealed record ParsedSkillFrontmatter(IReadOnlyDictionary<string, object?> Data, string Body);

internal static class SkillFrontmatter
{
    private static readonly Regex IntPattern = new("^[-+]?[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex FloatPattern = new(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.Compiled);

    public static ParsedSkillFrontmatter? Parse(string raw)
    {
        var firstLineEnd = raw.IndexOf('\n');
        if (firstLineEnd < 0)
            return null;
        var firstLine = raw[..firstLineEnd].TrimEnd('\r');
        if (firstLine != "---")
            return null;
        var start = firstLineEnd + 1;
        var closing = FindClosing(raw, start);
        if (closing is null)
            return null;
        var data = ParseYaml(raw[start..closing.Value.Start]);
        return data is null ? null : new ParsedSkillFrontmatter(data, raw[closing.Value.BodyStart..]);
    }

    private static (int Start, int BodyStart)? FindClosing(string raw, int start)
    {
        var lineStart = start;
        while (lineStart <= raw.Length)
        {
            var nextNewline = raw.IndexOf('\n', lineStart);
            var lineEnd = nextNewline < 0 ? raw.Length : nextNewline;
            var line = raw[lineStart..lineEnd].TrimEnd('\r');
            if (line == "---")
                return (lineStart, nextNewline < 0 ? raw.Length : nextNewline + 1);
            if (nextNewline < 0)
                return null;
            lineStart = nextNewline + 1;
        }
        return null;
    }

    private static Dictionary<string, object?>? ParseYaml(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return null;
        return ConvertMapping(root);
    }

    private static object? ConvertNode(YamlNode node)
        => node switch
        {
            YamlMappingNode mapping => ConvertMapping(mapping),
            YamlSequenceNode sequence => sequence.Children.Select(ConvertNode).ToList(),
            YamlScalarNode scalar => ResolveScalar(scalar),
            _ => null,
        };

    private static Dictionary<string, object?> ConvertMapping(YamlMappingNode mapping)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in mapping.Children)
            result[$"{ConvertNode(key)}"] = ConvertNode(value);
        return result;
    }

    private static object? ResolveScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style != ScalarStyle.Plain)
            return value;
        return value switch
        {
            "null" or "~" or "" => null,
            "true" => true,
            "false" => false,
            _ when IntPattern.IsMatch(value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
            _ when FloatPattern.IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) => floating,
            _ => value,
        };
    }

    public static string? StringField(IReadOnlyDictionary<string, object?> data, string key)
        => data.TryGetValue(key, out var value) && value is string text && text.Length > 0 ? text : null;

    public static SkillInvocationPolicy ParseInvocationPolicy(IReadOnlyDictionary<string, object?> data)
    {
        RejectLegacyInvocationKey(data, "disableModelInvocation", "disable-model-invocation");
        RejectLegacyInvocationKey(data, "modelInvocable", "disable-model-invocation");
        RejectLegacyInvocationKey(data, "userInvocable", "user-invocable");
        var disableModelInvocation = FrontmatterBoolean(data, "disable-model-invocation");
        var userInvocable = FrontmatterBoolean(data, "user-invocable");
        return new SkillInvocationPolicy(disableModelInvocation != true, userInvocable != false);
    }

    private static void RejectLegacyInvocationKey(IReadOnlyDictionary<string, object?> data, string legacy, string canonical)
    {
        if (data.ContainsKey(legacy))
            throw new InvalidOperationException($"frontmatter field \"{legacy}\" is unsupported; use \"{canonical}\"");
    }

    private static bool? FrontmatterBoolean(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value))
            return null;
        return value switch
        {
            bool boolean => boolean,
            long integer when integer is 0 or 1 => integer == 1,
            string text when text is "1" or "0" => text == "1",
            string text => text.ToLowerInvariant() switch
            {
                "true" or "yes" or "on" => true,
                "false" or "no" or "off" => false,
                _ => throw new InvalidOperationException($"frontmatter field \"{key}\" must be a boolean"),
            },
            _ => throw new InvalidOperationException($"frontmatter field \"{key}\" must be a boolean"),
        };
    }

    public static IReadOnlyDictionary<string, object?>? OptionalMetadata(IReadOnlyDictionary<string, object?> data)
        => data.TryGetValue("metadata", out var value) && value is Dictionary<string, object?> metadata ? metadata : null;
}
