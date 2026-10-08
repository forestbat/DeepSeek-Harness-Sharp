using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Plugins.Native.Host;

/** 把原生 ABI 插件桥接成托管插件定义:激活时按句柄登记工具,运行期可增删,故障时标记 Faulted 并快速卸载。 */
public static class NativePluginBridge
{
    private static readonly JsonObject ResultSchema = Parse("""
        { "type": "object", "additionalProperties": true, "properties": { "text": { "type": "string" } } }
        """);

    public static PluginDefinition CreateDefinition(INativePlugin plugin)
        => PluginDefinition.From((ctx, _) =>
        {
            var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
                ?? throw new InvalidOperationException($"native plugin \"{plugin.Package}\" requires the tools service");
            var logger = ctx.LoggerFor("native-plugin");
            var host = new Host(plugin, tools, logger);
            try
            {
                plugin.Activate(host);
            }
            catch
            {
                host.DisposeRegistrations();
                throw;
            }
            return host;
        }, name: plugin.Package);

    /** 把原生插件包装成 IDshPlugin,使插件目录只保留一种登记形态。 */
    public static IDshPlugin AsPlugin(INativePlugin plugin) => new NativePluginAdapter(plugin);

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static string Render(JsonElement value)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? ""
                : value.GetRawText();

    private sealed class Host(INativePlugin plugin, ToolRuntime tools, Logger logger) : INativePluginHost, IDisposable
    {
        private readonly Dictionary<int, IDisposable> _registrations = [];
        private readonly Lock _sync = new();
        private bool _disposed;

        public void Log(int level, string message)
        {
            var line = $"native plugin \"{plugin.Package}\": {message}";
            switch (level)
            {
                case DshNativePluginAbi.LogError:
                    logger.Error("%s", line);
                    break;
                case DshNativePluginAbi.LogWarn:
                    logger.Warn("%s", line);
                    break;
                case DshNativePluginAbi.LogDebug:
                    logger.Debug("%s", line);
                    break;
                default:
                    logger.Info("%s", line);
                    break;
            }
        }

        public void RegisterTool(int handle, string name, string description, string parametersJson, Func<string, string?> invoke)
        {
            JsonObject parameters;
            try
            {
                parameters = JsonNode.Parse(parametersJson)?.AsObject() ?? [];
            }
            catch (JsonException error)
            {
                throw new InvalidOperationException(
                    $"native plugin \"{plugin.Package}\" tool \"{name}\" has an invalid parameters schema: {error.Message}");
            }
            var registration = tools.Register(new ToolDefinition
            {
                Name = name,
                Description = description,
                Parameters = parameters,
                Output = new ToolOutputDefinition(ResultSchema, (_, value) => [new TextBlock(Render(value))]),
                Execute = (args, _) => ExecuteTool(name, invoke, args),
            });
            lock (_sync)
                _registrations[handle] = registration;
        }

        public bool UnregisterTool(int handle)
        {
            IDisposable? registration;
            lock (_sync)
            {
                if (!_registrations.Remove(handle, out registration))
                    return false;
            }
            registration.Dispose();
            return true;
        }

        private Task<object?> ExecuteTool(string name, Func<string, string?> invoke, JsonElement args)
        {
            if (plugin.State == NativePluginState.Faulted)
                throw new InvalidOperationException($"native plugin \"{plugin.Package}\" is faulted and no longer callable");
            try
            {
                var result = invoke(args.GetRawText());
                if (result is null)
                    throw new InvalidOperationException($"native plugin \"{plugin.Package}\" tool \"{name}\" failed");
                return Task.FromResult<object?>(JsonDocument.Parse(result).RootElement.Clone());
            }
            catch (Exception error)
            {
                logger.Error("%s", $"native plugin \"{plugin.Package}\" tool \"{name}\" invoke failed: {error.Message}");
                Fault(name, error);
                throw;
            }
        }

        /** 故障遏制(进程内、按插件):标记 Faulted + 移除其全部工具 + 尽力卸载;二次同类故障由 quarantine 禁装。 */
        private void Fault(string tool, Exception error)
        {
            DisposeRegistrations();
            plugin.RecordFault($"tool \"{tool}\": {error.Message}");
            try
            {
                plugin.Dispose();
            }
            catch (Exception disposeError)
            {
                logger.Error("%s", $"native plugin \"{plugin.Package}\" faulted unload failed: {disposeError.Message}");
            }
        }

        public void DisposeRegistrations()
        {
            IDisposable[] registrations;
            lock (_sync)
            {
                registrations = [.. _registrations.Values];
                _registrations.Clear();
            }
            foreach (var registration in registrations)
                registration.Dispose();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            DisposeRegistrations();
            plugin.Deactivate();
        }
    }

    private sealed class NativePluginAdapter(INativePlugin plugin) : IDshPlugin
    {
        public string[] Inject => [];

        public IDisposable Apply(Context ctx, object? config)
            => (IDisposable)CreateDefinition(plugin).Apply(ctx, config)!;
    }
}
