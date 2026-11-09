using Dsh.Runtime;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Dsh.Boot;

/** The GUI edits the same typed YAML maps as settings.yaml, with no line-string grammar. */
public static class RuleMapYaml
{
    public static Dictionary<string, PermissionMatcher> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var stream = Load(text);
        ValidateMap(stream.Documents[0].RootNode);
        var map = new StaticDeserializerBuilder(new DshYamlStaticContext())
            .WithTypeConverter(new RuleToolsYamlConverter()).Build()
            .Deserialize<Dictionary<string, PermissionMatcher>>(text);
        PermissionRules.ValidateMap(map);
        return map;
    }

    public static string Format(Dictionary<string, PermissionMatcher> rules)
        => new StaticSerializerBuilder(new DshYamlStaticContext())
            .WithTypeConverter(new RuleToolsYamlConverter())
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .WithQuotingNecessaryStrings().Build().Serialize(rules);

    internal static void ValidateDocument(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var stream = Load(text);
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return;
        ValidatePolicy(Get(root, "safety"), structural: false);
        var scriptNode = Get(root, "scripts");
        if (scriptNode is null || scriptNode is YamlScalarNode { Value: null or "" or "null" or "~" }) return;
        try
        {
            if (scriptNode is not YamlMappingNode scripts
                || scripts.Children.Keys.Any(key => key is not YamlScalarNode { Value: "permissions" }))
                throw new ArgumentException("scripts must be a mapping containing only permissions.");
            ValidatePolicy(Get(scripts, "permissions"), structural: true);
        }
        catch (ArgumentException error)
        {
            throw new Dsh.Runtime.RuntimeException("INVALID_SCRIPT_SETTING", error.Message);
        }
    }

    private static YamlStream Load(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count != 1) throw new ArgumentException("Expected one YAML document.");
        return stream;
    }

    private static YamlNode? Get(YamlMappingNode map, string key)
        => map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    private static void ValidatePolicy(YamlNode? node, bool structural)
    {
        if (node is null || node is YamlScalarNode { Value: null or "" or "null" or "~" }) return;
        if (node is not YamlMappingNode map) throw new ArgumentException("Permission policies must be YAML mappings.");
        foreach (var (keyNode, value) in map.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value;
            if (key is "blacklist" or "whitelist") ValidateMap(value);
            else
            {
                if (key != "autoApprove" && !(structural && key is ("read" or "write" or "network")))
                    throw new ArgumentException("Unknown permission policy field.");
                if (value is not YamlScalarNode scalar || scalar.Style != YamlDotNet.Core.ScalarStyle.Plain
                    || !bool.TryParse(scalar.Value, out _))
                    throw new ArgumentException("Structural grants and autoApprove must be YAML booleans.");
            }
        }
    }

    private static void ValidateMap(YamlNode node)
    {
        if (node is not YamlMappingNode map)
            throw new ArgumentException("Rules must be an ID-to-matcher YAML map; legacy lists are not supported.");
        foreach (var (id, rule) in map.Children)
        {
            if (id is not YamlScalarNode || rule is not YamlMappingNode matcher)
                throw new ArgumentException("Each rule requires a nonempty ID and a matcher mapping.");
            foreach (var (keyNode, value) in matcher.Children)
            {
                var key = (keyNode as YamlScalarNode)?.Value;
                if (key is not ("tool" or "dialect" or "path" or "intent" or "args"))
                    throw new ArgumentException("Unknown matcher field; use tool, dialect, path, intent or args.");
                if (key == "args")
                {
                    if (value is not YamlMappingNode args) throw new ArgumentException("args must be a string-pattern mapping.");
                    foreach (var (argument, pattern) in args.Children)
                        if (argument is not YamlScalarNode || pattern is not YamlScalarNode)
                            throw new ArgumentException("args must contain string patterns; paths use the unified path field.");
                }
                else if (key == "tool")
                {
                    if (value is not YamlScalarNode && value is not YamlSequenceNode)
                        throw new ArgumentException("tool must be a scalar name or a sequence of names.");
                }
                else if (value is not YamlScalarNode)
                    throw new ArgumentException("Matcher dimensions other than tool and args must be scalar strings.");
            }
        }
    }
}
