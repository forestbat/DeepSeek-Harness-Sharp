namespace Cordis.Loader;

public sealed class Entry
{
    public Loader Loader { get; }
    public Context Ctx { get; }
    public Fiber? Fiber { get; internal set; }
    public EntryGroup Parent { get; internal set; } = null!;
    public EntryOptions Options { get; private set; } = new();
    public EntryGroup? Subgroup { get; internal set; }
    public EntryTree? Subtree { get; internal set; }
    public LocalRealm Realm { get; internal set; } = null!;

    internal Task? InitTask;

    public Entry(Loader loader)
    {
        Loader = loader;
        Ctx = loader.Ctx.Extend((Symbols.Entry, this));
        Ctx.Events.Emit(null, "loader/entry-init", this);
    }

    public string Id
    {
        get
        {
            var id = Options.Id ?? "";
            if (Parent.Tree.Ctx.Fiber.Entry is { } parentEntry)
            {
                id = parentEntry.Id + EntryTree.Sep + id;
            }
            return id;
        }
    }

    public async ValueTask<bool> IsDisabled()
    {
        if (Options.Group) return false;
        var entry = (Entry?)this;
        while (entry is not null)
        {
            if (await DisabledOf(entry.Options)) return true;
            entry = entry.Parent.Ctx.Fiber.Entry;
        }
        return false;
    }

    private async ValueTask<bool> DisabledOf(EntryOptions options)
    {
        if (options.Disabled is JsExpr expr)
        {
            var result = await Evaluate(expr.Expr);
            return result is true;
        }
        return options.Disabled is true;
    }

    public ValueTask<object?> Evaluate(string expr)
    {
        var importer = Loader.Importer
            ?? throw new CordisException("NO_IMPORTER", "cannot evaluate expression: no module importer configured");
        return importer.Evaluate(Ctx, expr);
    }

    internal async ValueTask<object?> ResolveConfig(object plugin)
    {
        if (plugin is Type type && type == typeof(Group)) return Options.Config;
        if (Options.Config is null) return null;
        return await Interpolate(Ctx, Options.Config);
    }

    internal static async ValueTask<object?> Interpolate(Context ctx, object? value)
    {
        switch (value)
        {
            case JsExpr expr:
                var importer = ctx.Get<Loader>("loader")?.Importer
                    ?? throw new CordisException("NO_IMPORTER", "cannot interpolate: no module importer configured");
                return await importer.Evaluate(ctx, expr.Expr);
            case List<object?> list:
                var result = new List<object?>(list.Count);
                foreach (var item in list) result.Add(await Interpolate(ctx, item));
                return result;
            case Dictionary<string, object?> dict:
                var mapped = new Dictionary<string, object?>(dict.Count);
                foreach (var (key, item) in dict) mapped[key] = await Interpolate(ctx, item);
                return mapped;
            default:
                return value;
        }
    }

    private async Task PatchContext(List<string> diff)
    {
        await Ctx.Events.Waterfall(this, "loader/patch-context", [this], async () =>
        {
            Ctx.SetPrototype(Parent.Ctx);
            if (Fiber?.Uid is not null && (diff.Contains("config") || Options.Group))
            {
                Fiber.Update(await ResolveConfig(Fiber.Runtime!.Callback), true);
            }
            return null;
        });
    }

    public async Task Refresh()
    {
        if (Fiber is not null) return;
        if (await IsDisabled()) return;
        await Init();
    }

    public async Task Update(EntryOptions options, bool create = false, bool force = false)
    {
        var legacy = Options;
        Options = create ? options : MergeOptions(Options, options);
        Options.SortKeys();

        if (await IsDisabled())
        {
            if (Fiber is not null) await Fiber.Dispose();
            return;
        }

        if (Fiber?.Uid is not null)
        {
            var diff = legacy.Keys.Concat(Options.Keys).Distinct()
                .Where(key => !CordisUtils.DeepEqual(Options[key], legacy[key]))
                .ToList();
            if (diff.Count == 0 && !force) return;
            Ctx.Events.Emit(null, "loader/partial-dispose", this, legacy, true);
            await PatchContext(diff);
        }
        else
        {
            await Init();
        }
    }

    private static EntryOptions MergeOptions(EntryOptions current, EntryOptions patch)
    {
        var merged = current.Clone();
        foreach (var key in patch.Keys)
        {
            merged[key] = patch[key];
        }
        return merged;
    }

    public async Task Init()
    {
        try
        {
            await (InitTask ??= InitInternal());
        }
        finally
        {
            InitTask = null;
        }
        if (Fiber is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Fiber.Await();
                }
                catch
                {
                    // fiber.await 的失败已由 fiber 日志记录
                }
                finally
                {
                    if (Loader.GetTasks().Count == 0)
                    {
                        Ctx.Reflect.Notify(["loader"]);
                    }
                }
            });
        }
    }

    private async Task InitInternal()
    {
        object? exports;
        try
        {
            exports = await Parent.Tree.Import(Options.Name!);
        }
        catch (Exception error)
        {
            Ctx.Logger.Error("%s", error);
            return;
        }
        finally
        {
            InitTask = null;
        }
        var plugin = Loader.UnwrapExports(exports);
        await PatchContext([]);
        Loader.ShowLog(this, "apply");
        Fiber = Ctx.Registry.Plugin(plugin!, await ResolveConfig(plugin!));
    }

    internal void SetFiber(Fiber? fiber) => Fiber = fiber;
}
