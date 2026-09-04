namespace Cordis.Loader;

public class EntryGroup
{
    public Context Ctx { get; }
    public EntryTree Tree { get; }
    public List<EntryOptions> Data { get; internal set; } = [];

    public EntryGroup(Context ctx, EntryTree tree)
    {
        Ctx = ctx;
        Tree = tree;
        var entry = ctx.Fiber.Entry;
        if (entry is not null) entry.Subgroup = this;
    }

    public async Task<string> Create(EntryOptions options)
    {
        var id = Tree.EnsureId(options);
        if (!Tree.Store.TryGetValue(id, out var entry))
        {
            entry = Tree.Store[id] = new Entry(Tree.GetLoader());
        }
        entry.Parent = this;
        await entry.Update(options, true, true);
        return entry.Id;
    }

    public void Unlink(EntryOptions options) => Data.Remove(options);

    public void Remove(string id, bool isDispose = false)
    {
        if (!Tree.Store.TryGetValue(id, out var entry)) return;
        entry.Fiber?.Dispose();
        if (!isDispose)
        {
            Unlink(entry.Options);
        }
        Tree.Store.Remove(id);
        Ctx.Events.Emit(null, "loader/partial-dispose", entry, entry.Options, false);
    }

    public async Task Update(List<EntryOptions> config)
    {
        var oldConfig = Data;
        Data = config;
        var oldMap = oldConfig.Where(o => o.Id is not null).ToDictionary(o => o.Id!, o => o);
        var newMap = new Dictionary<string, EntryOptions>();
        foreach (var options in config)
        {
            newMap[options.Id ?? $"anonymous:{Guid.NewGuid():N}"] = options;
        }

        var ids = oldMap.Keys.Concat(newMap.Keys).Distinct().ToList();
        await Task.WhenAll(ids.Select(async id =>
        {
            if (newMap.TryGetValue(id, out var options))
            {
                try
                {
                    await Create(options);
                }
                catch (Exception error)
                {
                    Ctx.Logger.Error("%s", error);
                }
            }
            else
            {
                Remove(id);
            }
        }));
    }

    public void Stop()
    {
        foreach (var options in Data.ToList())
        {
            if (options.Id is not null) Remove(options.Id, true);
        }
    }
}

public class Group : EntryGroup, IAsyncInit
{
    private readonly List<EntryOptions> _config;

    public Group(Context ctx, object? config)
        : base(ctx, ctx.Fiber.Entry!.Parent.Tree)
    {
        _config = Normalize(config);
        Ctx.On("internal/update", (thisArg, args) =>
        {
            _ = Update(Normalize(args[0]));
            return new ValueTask<object?>();
        });
    }

    private static List<EntryOptions> Normalize(object? config)
    {
        if (config is List<object?> list)
        {
            return list.Select(EntryOptions.From).ToList();
        }
        if (config is List<EntryOptions> options) return options;
        return [];
    }

    public async IAsyncEnumerable<object?> Init()
    {
        yield return (Action)(() => Stop());
        await Update(_config);
    }
}
