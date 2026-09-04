namespace Cordis.Loader;

public interface IModuleImporter
{
    Task<object?> Import(string specifier, string? baseUrl);

    ValueTask<object?> Evaluate(Context ctx, string expr);
}

public abstract class EntryTree
{
    public const string Sep = ":";

    public Context Ctx { get; }
    public bool EnableLogs { get; set; }
    public EntryGroup Root { get; }
    public Dictionary<string, Entry> Store { get; } = new();

    public IModuleImporter? Importer { get; set; }

    internal Loader GetLoader() => this as Loader ?? Ctx.Get<Loader>("loader")
        ?? throw new CordisException("NO_LOADER", "loader service is not available");

    protected EntryTree(Context ctx)
    {
        Ctx = ctx.Extend((Context.BaseUrlKey, ctx.BaseUrl));
        Root = new EntryGroup(Ctx, this);
        var entry = Ctx.Fiber.Entry;
        if (entry is not null) entry.Subtree = this;
    }

    public IEnumerable<Entry> Entries()
    {
        foreach (var entry in Store.Values)
        {
            yield return entry;
            if (entry.Subtree is null) continue;
            foreach (var child in entry.Subtree.Entries()) yield return child;
        }
    }

    public List<Task> GetTasks()
    {
        return Entries()
            .Select(entry => (Task?)entry.InitTask ?? entry.Fiber?.Inertia)
            .Where(task => task is not null)
            .Cast<Task>()
            .ToList();
    }

    public async Task Await()
    {
        while (true)
        {
            var tasks = GetTasks();
            if (tasks.Count == 0) return;
            await Task.WhenAll(tasks.Select(Safely));
        }

        static async Task Safely(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
                // Promise.allSettled
            }
        }
    }

    public string EnsureId(EntryOptions options)
    {
        if (options.Id is not null) return options.Id;
        string id;
        do
        {
            id = Random.Shared.NextInt64(0x100000000L).ToString("x8");
        } while (Store.ContainsKey(id));
        options.Id = id;
        return id;
    }

    public Entry Resolve(string id)
    {
        var parts = id.Split(Sep);
        EntryTree? tree = this;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            tree = tree.Store.TryGetValue(parts[i], out var child) ? child.Subtree : null;
            if (tree is null) throw new CordisException("ENTRY_NOT_FOUND", $"cannot resolve entry {id}");
        }
        if (!tree.Store.TryGetValue(parts[^1], out var entry))
        {
            throw new CordisException("ENTRY_NOT_FOUND", $"cannot resolve entry {id}");
        }
        return entry;
    }

    public EntryGroup ResolveGroup(string? id)
    {
        if (id is null) return Root;
        var entry = Resolve(id);
        return entry.Subgroup ?? throw new CordisException("NOT_A_GROUP", $"entry {id} is not a group");
    }

    public async Task<string> Create(EntryOptions options, string? parent = null, int position = int.MaxValue)
    {
        var group = ResolveGroup(parent);
        var index = Math.Min(position, group.Data.Count);
        group.Data.Insert(index, options);
        group.Tree.Write();
        return await group.Create(options);
    }

    public void Remove(string id)
    {
        var entry = Resolve(id);
        entry.Parent.Remove(id);
        entry.Parent.Tree.Write();
    }

    public async Task Update(string id, EntryOptions options, string? parent = null, int? position = null)
    {
        var entry = Resolve(id);
        var source = entry.Parent;
        if (parent is not null || position is not null)
        {
            var target = ResolveGroup(parent);
            source.Unlink(entry.Options);
            target.Data.Insert(Math.Min(position ?? int.MaxValue, target.Data.Count), entry.Options);
            target.Tree.Write();
            entry.Parent = target;
        }
        source.Tree.Write();
        await entry.Update(options, false, true);
    }

    public virtual async Task<object?> Import(string name)
    {
        if (name.StartsWith("cordis:"))
        {
            var key = name["cordis:".Length..];
            if (Ctx.Get<Loader>("loader")?.Builtins.TryGetValue(key, out var builtin) == true) return builtin;
            throw new CordisException("BUILTIN_NOT_FOUND", $"builtin plugin {key} not found");
        }
        var importer = Importer ?? (this is not Loader ? GetLoader().Importer : null);
        if (importer is null)
        {
            throw new CordisException("NO_IMPORTER", $"cannot import {name}: no module importer configured");
        }
        return await importer.Import(name, Ctx.BaseUrl);
    }

    public abstract void Write();
}
