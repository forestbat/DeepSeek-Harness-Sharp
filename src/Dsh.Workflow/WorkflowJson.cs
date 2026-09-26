using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Workflow;

/** 工作流返回值是 realm 物化出的纯 JSON 图(字典/列表/基元):转成 JsonNode,
 *  工具结果即可走源生成序列化,不必把开放 object 图交给序列化器(AOT 无反射回退)。 */
internal static class WorkflowJson
{
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value)),
        ulong number => JsonValue.Create(number),
        float or double or decimal => JsonValue.Create(Convert.ToDouble(value)),
        IDictionary<string, object?> map => ToObject(map),
        IEnumerable<object?> items => ToArray(items),
        _ => throw new InvalidOperationException($"workflow value of type {value.GetType().Name} is not JSON data"),
    };

    private static JsonObject ToObject(IDictionary<string, object?> map)
    {
        var result = new JsonObject();
        foreach (var (key, value) in map)
            result[key] = ToNode(value);
        return result;
    }

    private static JsonArray ToArray(IEnumerable<object?> items)
    {
        var result = new JsonArray();
        foreach (var item in items)
            result.Add(ToNode(item));
        return result;
    }
}
