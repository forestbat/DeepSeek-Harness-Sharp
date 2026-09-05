using System.Collections;
using System.Dynamic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Workflow;

public sealed class MaterializeError(string path, string reason) : Exception($"{path}: {reason}")
{
    public string Path { get; } = path;
    public string Reason { get; } = reason;
}

public static class WorkflowRealm
{
    public static string RenderThrown(object? error)
    {
        try
        {
            if (error is Exception exception)
            {
                var exceptionMessage = exception.Message;
                return string.IsNullOrEmpty(exceptionMessage) ? exception.GetType().Name : exceptionMessage;
            }

            if (error is IDictionary<string, object?> dict
                && dict.TryGetValue("message", out var messageValue)
                && messageValue is string message
                && message.Length > 0)
            {
                return message;
            }

            return error?.ToString() ?? "<null>";
        }
        catch
        {
            return "[unrenderable thrown value]";
        }
    }

    public static object? MaterializeFromRealm(object? value, string root = "value")
    {
        try
        {
            return Materialize(value, root, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }
        catch (MaterializeError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new MaterializeError(root, $"reading the value threw: {RenderThrown(error)}");
        }
    }

    private static object? Materialize(object? value, string path, HashSet<object> seen)
    {
        switch (value)
        {
            case null:
            case bool:
            case string:
                return value;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return Convert.ToDouble(value);
            case float or double or decimal:
            {
                var number = Convert.ToDouble(value);
                if (double.IsNaN(number) || double.IsInfinity(number))
                    throw new MaterializeError(path, "non-finite numbers are not JSON data");
                return number;
            }
            case JsonElement element:
                return JsonElementToObject(element, path, seen);
            case JsonNode node:
                return JsonNodeToObject(node, path, seen);
            case IDictionary<string, object?> dictionary:
                return MaterializeObject(dictionary, path, seen);
            case IEnumerable enumerable when value is not string:
                return MaterializeArray(enumerable, path, seen);
            default:
                throw new MaterializeError(path, "only plain objects and arrays are JSON data (exotic prototype)");
        }
    }

    private static object? JsonElementToObject(JsonElement element, string path, HashSet<object> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                    return (double)integer;
                return element.GetDouble();
            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                    list.Add(JsonElementToObject(item, $"{path}[{list.Count}]", seen));
                return list;
            }
            case JsonValueKind.Object:
            {
                var dict = new Dictionary<string, object?>();
                foreach (var property in element.EnumerateObject())
                    dict[property.Name] = JsonElementToObject(property.Value, PropertyPath(path, property.Name), seen);
                return dict;
            }
            default:
                throw new MaterializeError(path, "unsupported JSON value");
        }
    }

    private static object? JsonNodeToObject(JsonNode? node, string path, HashSet<object> seen)
        => node is null ? null : JsonElementToObject(JsonNodeToElement(node), path, seen);

    private static JsonElement JsonNodeToElement(JsonNode node)
        => JsonDocument.Parse(node.ToJsonString()).RootElement;

    private static object? MaterializeObject(IDictionary<string, object?> value, string path, HashSet<object> seen)
    {
        if (seen.Add(value))
        {
            try
            {
                var result = new Dictionary<string, object?>();
                foreach (var (key, child) in value)
                    result[key] = Materialize(child, PropertyPath(path, key), seen);
                return result;
            }
            finally
            {
                seen.Remove(value);
            }
        }

        throw new MaterializeError(path, "circular references are not JSON data");
    }

    private static object? MaterializeArray(IEnumerable value, string path, HashSet<object> seen)
    {
        var list = new List<object?>();
        foreach (var item in value)
            list.Add(Materialize(item, $"{path}[{list.Count}]", seen));
        return list;
    }

    private static string PropertyPath(string path, string key) => $"{path}.{key}";
}