using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Cordis.Loader;

public static class YamlConfig
{
    public const string JsTag = "tag:yaml.org,2002:js";

    private static readonly Regex IntPattern = new("^[-+]?[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex FloatPattern = new(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.Compiled);

    public static List<object?> Load(string content)
    {
        var deserializer = new DeserializerBuilder()
            .WithTagMapping(JsTag, typeof(JsExpr))
            .WithTypeConverter(new JsExprConverter())
            .Build();
        var raw = deserializer.Deserialize<object>(content);
        return ConvertNode(raw) as List<object?> ?? [];
    }

    public static string Dump(List<object?> data)
    {
        var yaml = ToYamlNode(data);
        var serializer = new SerializerBuilder()
            .WithTypeConverter(new JsExprConverter())
            .Build();
        return serializer.Serialize(yaml);
    }

    private static object? ConvertNode(object? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsExpr:
                return node;
            case Dictionary<object, object?> dict:
                var mapped = new Dictionary<string, object?>();
                foreach (var (key, value) in dict)
                {
                    mapped[$"{key}"] = ConvertNode(value);
                }
                return mapped;
            case List<object?> list:
                return list.Select(ConvertNode).ToList();
            case string scalar:
                return ParseScalar(scalar);
            default:
                return node;
        }
    }

    private static object? ParseScalar(string value)
    {
        if (value is "null" or "~" or "") return null;
        if (value == "true") return true;
        if (value == "false") return false;
        if (IntPattern.IsMatch(value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }
        if (FloatPattern.IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating))
        {
            return floating;
        }
        return value;
    }

    private static object? ToYamlNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsExpr:
                return value;
            case EntryOptions options:
                return ToYamlNode(OptionsToDict(options));
            case IDictionary<string, object?> dict:
                var mapped = new Dictionary<object, object?>();
                foreach (var (key, item) in dict)
                {
                    mapped[key] = ToYamlNode(item);
                }
                return mapped;
            case IEnumerable<object?> list when value is not string:
                return list.Select(ToYamlNode).ToList();
            default:
                return value;
        }
    }

    private static Dictionary<string, object?> OptionsToDict(EntryOptions options)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var key in options.Keys)
        {
            dict[key] = options[key];
        }
        return dict;
    }

    private sealed class JsExprConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(JsExpr);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var scalar = parser.Consume<Scalar>();
            return new JsExpr(scalar.Value);
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var expr = (JsExpr)value!;
            emitter.Emit(new Scalar(null, JsTag, expr.Expr, ScalarStyle.Plain, true, false));
        }
    }
}
