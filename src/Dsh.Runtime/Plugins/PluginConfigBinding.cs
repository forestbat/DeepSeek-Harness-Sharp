using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Dsh.Runtime;

/**
 * 类型化插件配置绑定: 把插件包名对应的 parameters 段绑定到插件自带的配置类型。
 * 未知字段与类型错配通过 warn 上报(加载/激活期),绑定失败保持默认值,不中断组合。
 * 键按忽略大小写匹配属性名;嵌套映射递归绑定,告警路径带点号前缀(如 mongo.conectionString)。
 */
public static class PluginConfigBinding
{
    public static T Bind<T>(object? raw, Action<string>? warn = null) where T : new()
        => (T)Bind(typeof(T), raw, warn);

    public static object Bind(Type type, object? raw, Action<string>? warn = null)
    {
        if (raw is not null && type.IsInstanceOfType(raw))
            return raw;
        var instance = CreateInstance(type);
        if (raw is null)
            return instance;
        if (raw is not IReadOnlyDictionary<string, object?> map)
        {
            warn?.Invoke($"config for {type.Name} is not a mapping; defaults are used");
            return instance;
        }
        BindInto(instance, type, map, "", warn);
        return instance;
    }

    private static object CreateInstance(Type type)
    {
        try
        {
            return Activator.CreateInstance(type)
                ?? throw new RuntimeException("CONFIG_TYPE_NOT_CREATABLE", $"config type {type.Name} could not be created");
        }
        catch (MissingMethodException)
        {
            throw new RuntimeException("CONFIG_TYPE_NOT_CREATABLE", $"config type {type.Name} needs a public parameterless constructor");
        }
    }

    private static void BindInto(object instance, Type type, IReadOnlyDictionary<string, object?> map, string path, Action<string>? warn)
    {
        var properties = WritableProperties(type);
        foreach (var (key, value) in map)
        {
            var property = properties.FirstOrDefault(candidate => string.Equals(candidate.Name, key, StringComparison.OrdinalIgnoreCase));
            if (property is null)
            {
                warn?.Invoke($"unknown config field '{path}{key}' for {type.Name}; known fields: {string.Join(", ", properties.Select(candidate => candidate.Name))}");
                continue;
            }
            if (TryConvert(value, property.PropertyType, $"{path}{key}", warn, out var converted))
                property.SetValue(instance, converted);
        }
    }

    private static PropertyInfo[] WritableProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanWrite).ToArray();

    private static bool TryConvert(object? value, Type target, string path, Action<string>? warn, out object? converted)
    {
        converted = null;
        if (value is null)
            return AcceptsNull(target) || Fail(warn, path, target, "null");
        if (target.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }
        if (Nullable.GetUnderlyingType(target) is { } underlying)
            return TryConvert(value, underlying, path, warn, out converted);
        if (target == typeof(string))
            return Fail(warn, path, target, value.GetType().Name);
        if (TryConvertScalar(value, target, out converted))
            return true;
        if (TryConvertCollection(value, target, path, warn, out converted))
            return true;
        if (value is IReadOnlyDictionary<string, object?> nested && target.IsClass && !typeof(IEnumerable).IsAssignableFrom(target))
            return TryConvertNested(nested, target, path, warn, out converted);
        return Fail(warn, path, target, value.GetType().Name);
    }

    private static bool TryConvertScalar(object value, Type target, out object? converted)
    {
        converted = null;
        if (target == typeof(bool))
            return TryConvertBool(value, out converted);
        if (target.IsEnum)
            return TryConvertEnum(value, target, out converted);
        if (!IsNumeric(target) || value is bool || value is not IConvertible)
            return false;
        try
        {
            converted = Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryConvertBool(object value, out object? converted)
    {
        converted = null;
        if (value is bool flag)
        {
            converted = flag;
            return true;
        }
        if (value is string text && bool.TryParse(text, out var parsed))
        {
            converted = parsed;
            return true;
        }
        return false;
    }

    private static bool TryConvertEnum(object value, Type target, out object? converted)
    {
        converted = null;
        if (value is string text)
        {
            if (!Enum.TryParse(target, text, ignoreCase: true, out var parsed))
                return false;
            converted = parsed;
            return true;
        }
        if (value is IConvertible)
        {
            converted = Enum.ToObject(target, value);
            return true;
        }
        return false;
    }

    private static bool TryConvertCollection(object value, Type target, string path, Action<string>? warn, out object? converted)
    {
        converted = null;
        var element = ElementTypeOf(target);
        if (element is null)
            return false;
        var items = ItemsOf(value, element);
        if (items is null)
            return false;
        var convertedItems = new List<object?>(items.Count);
        foreach (var item in items)
        {
            if (!TryConvert(item, element, path, warn, out var itemConverted))
                return false;
            convertedItems.Add(itemConverted);
        }
        converted = MaterializeCollection(target, element, convertedItems);
        return converted is not null;
    }

    /** 集合元素展开: 逗号串按字符串集合语义切分,序列逐元素保留(标量转字符串集合时再不变换)。 */
    private static List<object?>? ItemsOf(object value, Type element)
    {
        if (value is string text)
            return element == typeof(string)
                ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Cast<object?>().ToList()
                : null;
        if (value is IEnumerable sequence)
            return sequence.Cast<object?>().ToList();
        return null;
    }

    private static object? MaterializeCollection(Type target, Type element, List<object?> items)
    {
        var array = items.Select(item => item ?? (element.IsValueType ? Activator.CreateInstance(element) : null)).ToArray();
        var typed = Array.CreateInstance(element, array.Length);
        array.CopyTo(typed, 0);
        if (target.IsArray)
            return typed;
        if (target.IsAssignableFrom(typeof(List<>).MakeGenericType(element)))
            return Activator.CreateInstance(typeof(List<>).MakeGenericType(element), typed);
        if (target.IsAssignableFrom(typeof(HashSet<>).MakeGenericType(element)))
            return Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(element), typed);
        return null;
    }

    private static bool TryConvertNested(IReadOnlyDictionary<string, object?> map, Type target, string path, Action<string>? warn, out object? converted)
    {
        converted = null;
        try
        {
            converted = CreateInstance(target);
        }
        catch (RuntimeException)
        {
            return false;
        }
        BindInto(converted, target, map, $"{path}.", warn);
        return true;
    }

    private static Type? ElementTypeOf(Type target)
    {
        if (target.IsArray)
            return target.GetElementType();
        return target.IsGenericType && target.GetGenericTypeDefinition() is var definition && IsCollectionDefinition(definition)
            ? target.GetGenericArguments()[0]
            : null;
    }

    private static bool IsCollectionDefinition(Type definition)
        => definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IReadOnlyList<>)
            || definition == typeof(IEnumerable<>) || definition == typeof(ICollection<>)
            || definition == typeof(HashSet<>) || definition == typeof(ISet<>) || definition == typeof(IReadOnlySet<>);

    private static bool IsNumeric(Type type)
        => Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
            or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
            or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    private static bool AcceptsNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static bool Fail(Action<string>? warn, string path, Type target, string actual)
    {
        warn?.Invoke($"config field '{path}' expects {FriendlyName(target)}, got {actual}; default value is kept");
        return false;
    }

    private static string FriendlyName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return $"{FriendlyName(underlying)}?";
        if (!type.IsGenericType)
            return type.Name;
        var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        var name = tick > 0 ? type.Name[..tick] : type.Name;
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyName))}>";
    }
}
