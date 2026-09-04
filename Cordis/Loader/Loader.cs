namespace Cordis.Loader;

public sealed class LoaderConfig
{
    public string? BaseUrl { get; init; }
}

public class Loader : EntryTree
{
    public const string ServiceName = "loader";

    public Dictionary<string, object?> Builtins { get; } = new();

    public Loader(Context ctx, LoaderConfig? config = null) : base(ctx)
    {
        if (config?.BaseUrl is not null)
        {
            ctx.BaseUrl = config.BaseUrl;
        }

        ctx.Reflect.Provide(ServiceName, this, Check);

        ctx.On("internal/update", (thisArg, args) =>
        {
            var fiber = (Fiber)thisArg!;
            var config1 = args[0];
            var noSave = args[1] is true;
            var next = (Func<object?>)args[2]!;
            if (fiber.Entry is null || noSave || fiber.Parent.Fiber?.Entry == fiber.Entry) return new ValueTask<object?>(next());
            fiber.Entry.Options.Config = config1;
            fiber.Entry.Parent.Tree.Write();
            return new ValueTask<object?>(next());
        }, new EventOptions { Global = true, Prepend = true });

        ctx.On("internal/update", (thisArg, args) =>
        {
            var fiber = (Fiber)thisArg!;
            var next = (Func<object?>)args[2]!;
            if (fiber.Entry is null || fiber.Parent.Fiber?.Entry == fiber.Entry) return new ValueTask<object?>(next());
            ShowLog(fiber.Entry, "reload");
            return new ValueTask<object?>(next());
        }, new EventOptions { Global = true });

        ctx.On(EventNames.Plugin, (thisArg, args) =>
        {
            var fiber = (Fiber)args[0]!;
            OnPlugin(fiber, ctx);
            return new ValueTask<object?>();
        });

        ctx.Plugin(IsolatePlugin.Definition);
    }

    public override void Write()
    {
        // Loader 的根树在内存中，不写盘
    }

    private bool Check()
    {
        var awaitFlag = false;
        foreach (var level in Ctx.InterceptMap.Chain())
        {
            if (!level.HasOwn(ServiceName)) continue;
            if (level[ServiceName] is IDictionary<string, object?> dict
                && dict.GetOrNull("await") is true)
            {
                awaitFlag = true;
            }
        }
        return !awaitFlag || GetTasks().Count == 0;
    }

    public void ShowLog(Entry entry, string type)
    {
        if (entry.Options.Group || !entry.Parent.Tree.EnableLogs) return;
        Ctx.Root.Logger.Invoke(ServiceName).Info("%s plugin %C", type, entry.Options.Name);
    }

    public string? Locate(Fiber? fiber = null)
    {
        fiber ??= Ctx.Fiber;
        while (true)
        {
            if (fiber.Entry is not null) return fiber.Entry.Id;
            var next = fiber.Parent.Fiber;
            if (ReferenceEquals(next, fiber)) return null;
            fiber = next;
        }
    }

    public virtual void Exit()
    {
    }

    public object? UnwrapExports(object? exports) => exports;

    private void OnPlugin(Fiber fiber, Context ctx)
    {
        if (fiber.Parent.GetProp(Symbols.Entry) is Entry parentEntry && fiber.Entry is null)
        {
            fiber.Entry = parentEntry;
            if (fiber.Entry.Options.Inject is not null)
            {
                foreach (var (name, value) in fiber.Entry.Options.Inject)
                {
                    fiber.Inject[name] = value;
                }
            }
        }

        if (fiber.Uid is not null) return;
        if (fiber.Entry is null) return;
        if (fiber.Parent.Fiber?.Entry == fiber.Entry) return;
        if (ctx.Registry.Has(fiber.Runtime!.Callback)) return;
        if (fiber.Entry.Parent.Tree.Ctx.Fiber.Uid is null) return;

        ShowLog(fiber.Entry, "unload");
        if (fiber.Entry.Options.Disabled is true) return;

        fiber.Entry.Options.Disabled = true;
        fiber.Entry.Parent.Tree.Write();
    }
}
