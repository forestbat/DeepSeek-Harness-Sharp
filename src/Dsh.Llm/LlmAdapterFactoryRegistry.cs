using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Llm;

/** 适配器工厂登记表:适配器插件在此声明能服务的 wire 类型,宿主按 settings.yaml 的 type 选用。
 *  同一 wire 由多个工厂声明时取先登记者。 */
public sealed class LlmAdapterFactoryRegistry(Context ctx) : Service(ctx, ServiceName)
{
    public const string ServiceName = "llmAdapterFactories";

    private sealed record Entry(string Source, ILlmAdapterFactory Factory);

    private readonly List<Entry> _factories = [];
    private readonly Lock _sync = new();

    public IDisposable Register(string source, ILlmAdapterFactory factory)
    {
        var entry = new Entry(source, factory);
        lock (_sync)
            _factories.Add(entry);
        Ctx.Emit(new LlmAdapterFactoriesUpdatedNotification());
        return new Subscription(() =>
        {
            bool removed;
            lock (_sync)
                removed = _factories.Remove(entry);
            if (removed)
                Ctx.Emit(new LlmAdapterFactoriesUpdatedNotification());
        });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    public bool TryResolve(string wire, out string source, out ILlmAdapterFactory? factory)
    {
        Entry[] entries;
        lock (_sync)
            entries = [.. _factories];
        foreach (var entry in entries)
        {
            foreach (var definition in entry.Factory.Wires)
            {
                if (!string.Equals(definition.Wire, wire, StringComparison.OrdinalIgnoreCase))
                    continue;
                source = entry.Source;
                factory = entry.Factory;
                return true;
            }
        }
        source = "";
        factory = null;
        return false;
    }

    public IReadOnlyList<(string Source, ILlmAdapterFactory Factory)> ListAll()
    {
        lock (_sync)
            return _factories.Select(entry => (entry.Source, entry.Factory)).ToList();
    }

    /** 某个工厂的 wire 定义(用于解析默认 baseUrl/apiKeyEnv)。 */
    public LlmWireDefinition? DefinitionFor(string wire)
    {
        Entry[] entries;
        lock (_sync)
            entries = [.. _factories];
        foreach (var entry in entries)
        {
            foreach (var definition in entry.Factory.Wires)
            {
                if (string.Equals(definition.Wire, wire, StringComparison.OrdinalIgnoreCase))
                    return definition;
            }
        }
        return null;
    }
}

public sealed record LlmAdapterFactoriesUpdatedNotification : INotification
{
    public static string EventName => "llm/adapter-factories-updated";
}
