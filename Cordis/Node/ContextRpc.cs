using System.Reflection;
using System.Text.Json.Nodes;

namespace Cordis.Node;

public sealed class BoundMethod(Func<object?[], object?> invoke)
{
    public object? Invoke(object?[] args) => invoke(args);
}

public static class ContextRpc
{
    private static readonly HashSet<string> ContextMethods =
    [
        "on", "once", "emit", "parallel", "serial", "bail", "waterfall",
        "plugin", "inject", "effect", "get", "set", "provide", "accessor",
        "mixin", "isolate", "intercept", "extend",
    ];

    public static object? HandleGet(NodeHost host, JsonObject parameters)
    {
        var id = parameters["id"]!.GetValue<long>();
        var prop = parameters["prop"]!.GetValue<string>();
        var target = host.Handles.Resolve(id)
            ?? throw new JsRemoteException($"handle {id} not found", null);
        return GetProp(host, target, prop);
    }

    private static object? GetProp(NodeHost host, object target, string prop)
    {
        switch (target)
        {
            case Context ctx:
                return GetContextProp(host, ctx, prop);
            case Fiber fiber:
                return GetFiberProp(host, fiber, prop);
            case LoggerService loggerService:
                return GetLoggerServiceProp(loggerService, prop);
            case Logger logger:
                return GetLoggerProp(logger, prop);
            case BoundMethod method:
                return method;
            default:
                return GetMemberProp(host, target, prop);
        }
    }

    private static object? GetContextProp(NodeHost host, Context ctx, string prop)
    {
        if (ContextMethods.Contains(prop))
        {
            return new BoundMethod(args => InvokeContextMethod(host, ctx, prop, args));
        }
        return prop switch
        {
            "fiber" => ctx.Fiber,
            "root" => ctx.Root,
            "baseUrl" => ctx.BaseUrl,
            "events" => ctx.Events,
            "registry" => ctx.Registry,
            "reflect" => ctx.Reflect,
            "logger" => ctx.Logger,
            _ => ctx.ResolveProperty(prop),
        };
    }

    private static object? GetFiberProp(NodeHost host, Fiber fiber, string prop)
    {
        return prop switch
        {
            "state" => fiber.State.ToString().ToLowerInvariant(),
            "name" => fiber.Name,
            "uid" => fiber.Uid,
            "config" => fiber.Config,
            "entry" => fiber.Entry is null ? null : host.Handles.Store(fiber.Entry),
            "dispose" => new BoundMethod(_ => fiber.Dispose()),
            "update" => new BoundMethod(args =>
            {
                fiber.Update(args.ElementAtOrDefault(0), args.ElementAtOrDefault(1) is true);
                return null;
            }),
            "restart" => new BoundMethod(_ => fiber.Restart()),
            "await" => new BoundMethod(_ => fiber.Await()),
            "getEffects" => new BoundMethod(_ => fiber.GetEffects()),
            _ => throw new JsRemoteException($"unknown fiber property {prop}", null),
        };
    }

    private static object? GetLoggerServiceProp(LoggerService service, string prop)
    {
        return prop switch
        {
            "error" or "warn" or "info" or "debug" => new BoundMethod(args =>
            {
                Log(service.Invoke(), prop, args);
                return null;
            }),
            "buffer" => service.Buffer.ToList(),
            "exporter" => new BoundMethod(args =>
            {
                throw new JsRemoteException("exporter from JS is not supported", null);
            }),
            _ => throw new JsRemoteException($"unknown logger property {prop}", null),
        };
    }

    private static object? GetLoggerProp(Logger logger, string prop)
    {
        return prop switch
        {
            "error" or "warn" or "info" or "debug" => new BoundMethod(args =>
            {
                Log(logger, prop, args);
                return null;
            }),
            "name" => logger.Name,
            _ => throw new JsRemoteException($"unknown logger property {prop}", null),
        };
    }

    private static void Log(Logger logger, string type, object?[] args)
    {
        var method = type switch
        {
            "error" => (Action<object?, object?[]>)logger.Error,
            "warn" => logger.Warn,
            "info" => logger.Info,
            _ => logger.Debug,
        };
        method(args.ElementAtOrDefault(0), args.Skip(1).ToArray());
    }

    private static object? GetMemberProp(NodeHost host, object target, string prop)
    {
        if (target is System.Collections.IDictionary dict && dict.Contains(prop))
        {
            return dict[prop];
        }
        var type = target.GetType();
        var property = type.GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
        if (property is not null) return property.GetValue(target);
        var field = type.GetField(prop, BindingFlags.Public | BindingFlags.Instance);
        if (field is not null) return field.GetValue(target);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == prop)
            .ToList();
        if (methods.Count > 0)
        {
            return new BoundMethod(args => InvokeMethod(target, methods, args));
        }
        throw new JsRemoteException($"member {prop} not found on {type.Name}", null);
    }

    private static object? InvokeMethod(object target, List<MethodInfo> overloads, object?[] args)
    {
        foreach (var method in overloads)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != args.Length) continue;
            if (TryConvert(parameters, args, out var converted))
            {
                return method.Invoke(target, converted);
            }
        }
        var fallback = overloads[0];
        var fallbackParameters = fallback.GetParameters();
        var fallbackArgs = new object?[fallbackParameters.Length];
        for (var i = 0; i < fallbackParameters.Length; i++)
        {
            fallbackArgs[i] = i < args.Length ? args[i] : fallbackParameters[i].HasDefaultValue ? fallbackParameters[i].DefaultValue : null;
        }
        return fallback.Invoke(target, fallbackArgs);
    }

    private static bool TryConvert(ParameterInfo[] parameters, object?[] args, out object?[] converted)
    {
        converted = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (!TryConvertValue(parameters[i].ParameterType, args[i], out converted[i])) return false;
        }
        return true;
    }

    internal static bool TryConvertValue(Type type, object? value, out object? converted)
    {
        converted = null;
        if (value is null || value is JsUndefined)
        {
            converted = type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;
            return true;
        }
        if (type.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }
        if (type == typeof(string))
        {
            converted = value.ToString();
            return true;
        }
        if (type == typeof(int) && value is long l)
        {
            converted = (int)l;
            return true;
        }
        if (type == typeof(long) && value is long)
        {
            converted = value;
            return true;
        }
        if (type == typeof(double) && value is long or double)
        {
            converted = Convert.ToDouble(value);
            return true;
        }
        if (type == typeof(bool) && value is bool)
        {
            converted = value;
            return true;
        }
        if (type == typeof(object))
        {
            converted = value;
            return true;
        }
        return false;
    }

    public static object? HandleSet(NodeHost host, JsonObject parameters)
    {
        var id = parameters["id"]!.GetValue<long>();
        var prop = parameters["prop"]!.GetValue<string>();
        var value = NodeMarshal.Unmarshal(host, parameters["value"]);
        var target = host.Handles.Resolve(id)
            ?? throw new JsRemoteException($"handle {id} not found", null);
        switch (target)
        {
            case Context ctx:
                if (prop == "baseUrl")
                {
                    ctx.BaseUrl = value as string;
                    return true;
                }
                ctx.SetProperty(prop, value);
                return true;
            case Fiber fiber when prop == "config":
                fiber.Config = value;
                return true;
            default:
                var type = target.GetType();
                var property = type.GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                if (property is not null && property.CanWrite)
                {
                    if (TryConvertValue(property.PropertyType, value, out var converted))
                    {
                        property.SetValue(target, converted);
                        return true;
                    }
                }
                var field = type.GetField(prop, BindingFlags.Public | BindingFlags.Instance);
                if (field is not null)
                {
                    if (TryConvertValue(field.FieldType, value, out var converted))
                    {
                        field.SetValue(target, converted);
                        return true;
                    }
                }
                throw new JsRemoteException($"member {prop} not settable on {type.Name}", null);
        }
    }

    public static object? HandleHas(NodeHost host, JsonObject parameters)
    {
        var id = parameters["id"]!.GetValue<long>();
        var prop = parameters["prop"]!.GetValue<string>();
        var target = host.Handles.Resolve(id);
        if (target is null) return false;
        switch (target)
        {
            case Context ctx:
                if (ContextMethods.Contains(prop)) return true;
                if (prop is "fiber" or "root" or "baseUrl" or "events" or "registry" or "reflect" or "logger") return true;
                if (ctx.Reflect.Props.ContainsKey(prop)) return true;
                return ctx.GetProp(prop) is not null;
            default:
                var type = target.GetType();
                return type.GetProperty(prop) is not null
                    || type.GetField(prop) is not null
                    || type.GetMethods().Any(m => m.Name == prop);
        }
    }

    public static async Task<object?> HandleCall(NodeHost host, JsonObject parameters)
    {
        var id = parameters["id"]!.GetValue<long>();
        var args = NodeMarshal.UnmarshalArgs(host, parameters["args"]);
        var target = host.Handles.Resolve(id)
            ?? throw new JsRemoteException($"handle {id} not found", null);
        var result = target switch
        {
            BoundMethod method => method.Invoke(args),
            LoggerService loggerService => loggerService.Invoke(args.ElementAtOrDefault(0) as string),
            Delegate func => InvokeDelegate(func, args),
            JsHandle handle => await handle.Call(args),
            _ => throw new JsRemoteException($"handle {id} is not callable", null),
        };
        return result;
    }

    private static object? InvokeDelegate(Delegate func, object?[] args)
    {
        var parameters = func.Method.GetParameters();
        var converted = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            converted[i] = i < args.Length ? args[i] : parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        }
        return func.DynamicInvoke(converted);
    }

    public static async Task<object?> HandleContextCall(NodeHost host, JsonObject parameters)
    {
        var id = parameters["id"]!.GetValue<long>();
        var method = parameters["method"]!.GetValue<string>();
        var args = NodeMarshal.UnmarshalArgs(host, parameters["args"]);
        var ctx = host.Handles.Resolve(id) as Context
            ?? throw new JsRemoteException($"context {id} not found", null);
        return await InvokeContextMethodAsync(host, ctx, method, args);
    }

    private static object? InvokeContextMethod(NodeHost host, Context ctx, string method, object?[] args)
    {
        return InvokeContextMethodAsync(host, ctx, method, args).GetAwaiter().GetResult();
    }

    private static async Task<object?> InvokeContextMethodAsync(NodeHost host, Context ctx, string method, object?[] args)
    {
        switch (method)
        {
            case "on":
            case "once":
            {
                var name = args[0]?.ToString() ?? throw new JsRemoteException("event name required", null);
                var listener = RequireJsHandle(args[1]);
                var options = ParseEventOptions(args.ElementAtOrDefault(2));
                EventListener callback = async (thisArg, innerArgs) =>
                    await host.InvokeCallbackAsync(listener.Id, thisArg, innerArgs);
                var dispose = method == "on" ? ctx.On(name, callback, options) : ctx.Once(name, callback, options);
                return (Func<bool>)(() => dispose());
            }
            case "emit":
            {
                ParseEventArgs(args, out var thisArg, out var name, out var rest);
                ctx.Events.Emit(thisArg, name, rest);
                return null;
            }
            case "parallel":
            {
                ParseEventArgs(args, out var thisArg, out var name, out var rest);
                return ctx.Events.Parallel(thisArg, name, rest);
            }
            case "serial":
            {
                ParseEventArgs(args, out var thisArg, out var name, out var rest);
                return await ctx.Events.Serial(thisArg, name, rest);
            }
            case "bail":
            {
                ParseEventArgs(args, out var thisArg, out var name, out var rest);
                var task = ctx.Events.Bail(thisArg, name, rest);
                return task.IsCompleted ? task.GetAwaiter().GetResult() : host.Promises.Track(task.AsTask());
            }
            case "waterfall":
            {
                ParseEventArgs(args, out var thisArg, out var name, out var rest);
                if (rest.Length == 0) throw new JsRemoteException("waterfall requires inner function", null);
                var inner = RequireJsHandle(rest[^1]);
                var innerArgs = rest[..^1];
                var task = ctx.Events.Waterfall(thisArg, name, innerArgs, async () =>
                    await host.InvokeCallbackAsync(inner.Id, null, []));
                return task.IsCompleted ? task.GetAwaiter().GetResult() : host.Promises.Track(task.AsTask());
            }
            case "plugin":
            {
                var plugin = ResolvePlugin(host, args[0]);
                var fiber = ctx.Registry.Plugin(plugin, args.ElementAtOrDefault(1));
                return fiber;
            }
            case "inject":
            {
                var deps = ParseInject(args[0]);
                var callback = RequireJsHandle(args[1]);
                var fiber = ctx.Registry.Inject(deps, (innerCtx, _) => InvokeEffectCallback(host, innerCtx, callback));
                return fiber;
            }
            case "effect":
            {
                var callback = RequireJsHandle(args[0]);
                var label = args.ElementAtOrDefault(1) as string ?? "anonymous";
                return ctx.Effect(() => Task.Run(() => RunEffectAsync(host, ctx, callback)), label);
            }
            case "get":
                return ctx.Get(args[0]?.ToString() ?? "", args.ElementAtOrDefault(1) is not false);
            case "set":
                ctx.SetProperty(args[0]?.ToString() ?? "", args.ElementAtOrDefault(1));
                return null;
            case "provide":
                return ctx.Provide(args[0]?.ToString() ?? "", args.ElementAtOrDefault(1));
            case "isolate":
                return ctx.Isolate(args[0]?.ToString() ?? "");
            case "intercept":
                return ctx.Intercept(args[0]?.ToString() ?? "", args.ElementAtOrDefault(1));
            case "extend":
                return ctx.Extend();
            default:
                throw new JsRemoteException($"unknown context method {method}", null);
        }
    }

    private static async Task<object?> InvokeEffectCallback(NodeHost host, Context ctx, JsHandle callback)
    {
        var result = await host.InvokeCallbackAsync(callback.Id, ctx, []);
        return result switch
        {
            null => null,
            JsHandle handle => (Func<Task>)(() => handle.Call()),
            _ => result,
        };
    }

    private static async Task<object?> RunEffectAsync(NodeHost host, Context ctx, JsHandle callback)
    {
        var result = await host.RequestAsync("runEffect", new JsonObject
        {
            ["ctx"] = host.Handles.Store(ctx),
            ["cb"] = callback.Id,
        });
        var disposes = new List<long>();
        if (result is Dictionary<string, object?> dict && dict.GetValueOrDefault("disposes") is IEnumerable<object?> ids)
        {
            disposes.AddRange(ids.OfType<long>());
        }
        return (Func<Task>)(async () =>
        {
            var array = new JsonArray();
            foreach (var handleId in disposes) array.Add(handleId);
            await host.RequestAsync("dispose", new JsonObject { ["handles"] = array });
        });
    }

    private static object ResolvePlugin(NodeHost host, object? pluginRef)
    {
        switch (pluginRef)
        {
            case IDictionary<string, object?> dict when dict.GetOrNull("$pk") is string key:
                return host.GetPlugin(key, null, false);
            case string key:
                return host.GetPlugin(key, null, false);
            case PluginDefinition or PluginCallback or Type:
                return pluginRef!;
            default:
                throw new JsRemoteException($"invalid plugin reference: {pluginRef}", null);
        }
    }

    private static JsHandle RequireJsHandle(object? value)
    {
        return value as JsHandle ?? throw new JsRemoteException("expected function handle", null);
    }

    private static EventOptions? ParseEventOptions(object? value)
    {
        if (value is bool b) return new EventOptions { Prepend = b };
        if (value is IDictionary<string, object?> dict)
        {
            return new EventOptions
            {
                Prepend = dict.GetOrNull("prepend") is true,
                Global = dict.GetOrNull("global") is true,
            };
        }
        return null;
    }

    private static void ParseEventArgs(object?[] args, out object? thisArg, out string name, out object?[] rest)
    {
        thisArg = null;
        var list = args.ToList();
        if (list.Count > 0 && list[0] is Context or Fiber)
        {
            thisArg = list[0];
            list.RemoveAt(0);
        }
        name = list.ElementAtOrDefault(0)?.ToString() ?? "";
        rest = list.Skip(1).ToArray();
    }

    private static Dictionary<string, object?> ParseInject(object? value)
    {
        var result = new Dictionary<string, object?>();
        switch (value)
        {
            case IEnumerable<object?> list when value is not string and not IDictionary<string, object?>:
                foreach (var item in list)
                {
                    if (item is not null) result[item.ToString()!] = null;
                }
                break;
            case IDictionary<string, object?> dict:
                foreach (var (key, item) in dict) result[key] = item;
                break;
        }
        return result;
    }
}
