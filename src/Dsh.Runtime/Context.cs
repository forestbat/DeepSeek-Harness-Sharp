using Dsh.Runtime.Ioc;
using Dsh.Runtime.Logging;
using Dsh.Runtime.Events;

namespace Dsh.Runtime;

public sealed class Context
{
    private static readonly object FilterKey = new();

    private readonly Dictionary<object, object?> _own = new(ReferenceEqualityComparer.Instance);
    private readonly Context? _prototype;
    private readonly PluginScheduler _scheduler;
    private EventsService? _events;
    private LoggerService? _logger;
    private IServiceProvider? _serviceProvider;

    public Context Root { get; }
    public PluginActivation Activation { get; }

    internal ServiceTable ServiceTable { get; }
    internal EventTable EventTable { get; }
    internal LoggingSetup Logging { get; }

    public PluginScheduler Scheduler => _scheduler;

    public Context() : this(LoggingSetup.Create(), new DryIocServiceRegistry())
    {
    }

    public Context(LoggingSetup logging) : this(logging, new DryIocServiceRegistry())
    {
    }

    public Context(LoggingSetup logging, IServiceRegistry services)
    {
        Logging = logging;
        Root = this;
        ServiceTable = new ServiceTable(services);
        EventTable = new EventTable();
        _scheduler = new PluginScheduler(this);
        Activation = PluginActivation.CreateRoot();
        Activation.Attach(this);
    }

    private Context(Context prototype, PluginActivation? activation = null)
    {
        _prototype = prototype;
        Root = prototype.Root;
        ServiceTable = prototype.ServiceTable;
        EventTable = prototype.EventTable;
        Logging = prototype.Logging;
        _scheduler = prototype._scheduler;
        Activation = activation ?? prototype.Activation;
    }

    public Context Extend(params (object Key, object? Value)[] meta)
    {
        var child = new Context(this);
        foreach (var (key, value) in meta)
        {
            child._own[key] = value;
        }
        return child;
    }

    internal Context CreatePluginContext(PluginActivation activation) => new(this, activation);

    public object? GetProp(object key)
    {
        for (var ctx = this; ctx is not null; ctx = ctx._prototype)
        {
            if (ctx._own.TryGetValue(key, out var value))
                return value;
        }
        return null;
    }

    public void SetOwn(object key, object? value) => _own[key] = value;

    public Context WithFilter(Func<Context, bool> filter) => Extend((FilterKey, filter));

    public Func<Context, bool>? Filter => GetProp(FilterKey) as Func<Context, bool>;

    public EventsService Events => _events ??= new EventsService(this, EventTable);

    public LoggerService Logger => _logger ??= new LoggerService(this, Logging);

    public Logger LoggerFor(string? name = null) => Logger.Invoke(name, this);

    public object? Get(string name, bool strict = true) => ServiceTable.Get(name, strict);

    /** 只读 IServiceProvider 视图,供生态库按类型取用已注册实例。 */
    public IServiceProvider ServiceProvider => _serviceProvider ??= new ServiceProviderView(ServiceTable.Registry);

    public T? Get<T>(string name, bool strict = true) where T : class => ServiceTable.Get(name, strict) as T;

    /** 类型化注入:按契约类型取唯一激活服务;多个候选抛 AMBIGUOUS_SERVICE(按名消歧),无候选返回 null。 */
    public T? Get<T>(bool strict = true) where T : class => ServiceTable.GetByType(typeof(T), strict) as T;

    public EffectHandle Provide(string name, object? value = null, Func<bool>? check = null)
        => ServiceTable.Provide(this, name, value, check);

    public Func<bool> On<TNotification>(INotificationHandler<TNotification> handler, EventOptions? options = null)
        where TNotification : INotification
        => Events.On(handler, options);

    public Func<bool> OnBail<TNotification>(IBailHandler<TNotification> handler, EventOptions? options = null)
        where TNotification : INotification
        => Events.OnBail(handler, options);

    public Func<bool> OnWaterfall<TNotification>(IWaterfallHandler<TNotification> handler, EventOptions? options = null)
        where TNotification : INotification
        => Events.OnWaterfall(handler, options);

    public Func<bool> On(Type notificationType, Func<object, ValueTask> handler, EventOptions? options = null)
        => Events.On(notificationType, handler, options);

    public void Emit<TNotification>(TNotification notification) where TNotification : INotification
        => Events.Emit(notification);

    public Task Parallel<TNotification>(TNotification notification) where TNotification : INotification
        => Events.Parallel(notification);

    public ValueTask<object?> Serial<TNotification>(TNotification notification) where TNotification : INotification
        => Events.Serial(notification);

    public ValueTask<object?> Waterfall<TNotification>(TNotification notification, Func<ValueTask<object?>> inner)
        where TNotification : INotification
        => Events.Waterfall(notification, inner);

    public EffectHandle Effect(Func<object?> execute, string label = "anonymous")
        => Activation.Effect(execute, label);

    public PluginActivation Plugin(PluginDefinition definition, object? config = null)
        => Root.Scheduler.Register(definition, config);

    internal bool IsServiceInjectable(string name) => ServiceTable.IsInjectable(name);

    internal bool IsServiceInjectable(Type type) => ServiceTable.IsInjectable(type);

    public override string ToString() => $"Context <{Activation.Name}>";
}
