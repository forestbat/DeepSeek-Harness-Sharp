namespace Cordis.Loader;

public enum IncludePatchOwnerKind
{
    File,
    Patch,
    Insert,
}

public sealed class IncludePatchOwner
{
    public IncludePatchOwnerKind Kind { get; }
    public int Index { get; }
    public IncludePatchInsert? Insert { get; }

    private IncludePatchOwner(IncludePatchOwnerKind kind, int index, IncludePatchInsert? insert)
    {
        Kind = kind;
        Index = index;
        Insert = insert;
    }

    public static IncludePatchOwner File { get; } = new(IncludePatchOwnerKind.File, -1, null);

    public static IncludePatchOwner Patch(int index) => new(IncludePatchOwnerKind.Patch, index, null);

    public static IncludePatchOwner InsertOwner(IncludePatchInsert insert) => new(IncludePatchOwnerKind.Insert, insert.Index, insert);
}

public sealed class IncludePatchInsert
{
    public required int Index { get; init; }
    public required List<object?> List { get; init; }
    public required object Raw { get; init; }
    public required string? Parent { get; init; }
}

public sealed class IncludePatchIndex
{
    private readonly Dictionary<string, Dictionary<string, int>> _keys = new();
    private readonly Dictionary<string, IncludePatchInsert> _inserts = new();

    public IncludePatchIndex(List<Dictionary<string, object?>>? patches)
    {
        var list = patches ?? [];
        for (var index = 0; index < list.Count; index++)
        {
            var patch = list[index];
            var id = patch.GetOrNull("id") as string;
            var insert = patch.GetOrNull("insert") as List<object?>;
            if (insert is not null)
            {
                WalkInsert(insert, id ?? null, index);
                continue;
            }
            if (id is null) continue;
            var keys = new Dictionary<string, int>();
            foreach (var key in patch.Keys)
            {
                if (key is "id" or "insert" or "name") continue;
                keys[key] = index;
            }
            _keys[id] = keys;
        }
    }

    public IncludePatchOwner Entry(string? id)
    {
        if (id is not null && _inserts.TryGetValue(id, out var insert)) return IncludePatchOwner.InsertOwner(insert);
        return IncludePatchOwner.File;
    }

    public IncludePatchOwner Key(string id, string key)
    {
        if (_inserts.TryGetValue(id, out var insert)) return IncludePatchOwner.InsertOwner(insert);
        if (_keys.TryGetValue(id, out var keys) && keys.TryGetValue(key, out var index)) return IncludePatchOwner.Patch(index);
        return IncludePatchOwner.File;
    }

    public bool FileOwned(string? id, string? key = null)
    {
        return key is null ? Entry(id).Kind == IncludePatchOwnerKind.File : Key(id!, key).Kind == IncludePatchOwnerKind.File;
    }

    private void WalkInsert(List<object?> list, string? parent, int index)
    {
        foreach (var item in list)
        {
            if (item is null) continue;
            var options = EntryOptions.From(item);
            if (options.Id is null) continue;
            _inserts[options.Id] = new IncludePatchInsert
            {
                Index = index,
                List = list,
                Raw = item,
                Parent = parent,
            };
            if (options.Group && options.Config is List<object?> children) WalkInsert(children, options.Id, index);
        }
    }
}

public readonly record struct IncludeRouteResult(bool FileChanged, bool PatchesChanged);

public static class IncludePatch
{
    public static IncludeRouteResult RouteJournal(
        Dictionary<string, IncludeJournalRecord> journal,
        List<EntryOptions> data,
        List<Dictionary<string, object?>> patches,
        Action<string, object?[]> warn)
    {
        var index = new IncludePatchIndex(patches);
        var fileChanged = false;
        var patched = false;

        foreach (var (id, record) in journal)
        {
            var owner = index.Entry(id);
            if (record.Kind == IncludeJournalKind.Remove)
            {
                if (owner.Kind == IncludePatchOwnerKind.Insert)
                {
                    owner.Insert!.List.Remove(owner.Insert.Raw);
                    patched = true;
                }
                else
                {
                    fileChanged |= IncludeJournal.Detach(data, id);
                }
                continue;
            }

            if (owner.Kind == IncludePatchOwnerKind.Insert)
            {
                ApplyChanges(owner.Insert!.Raw, record.Changes);
                patched = true;
                continue;
            }

            var flat = IncludeJournal.Flatten(data).GetValueOrDefault(id);
            if (flat is null)
            {
                var options = new EntryOptions { Id = id };
                IncludeJournal.ApplyChanges(options, record.Changes);
                if (IncludeJournal.Place(data, options, record.Relocated ? record.Parent : null, record.Position)) fileChanged = true;
                continue;
            }

            var fileChanges = new Dictionary<string, object?>();
            var patchChanges = new Dictionary<int, Dictionary<string, object?>>();
            foreach (var (key, value) in record.Changes)
            {
                var keyOwner = index.Key(id, key);
                if (keyOwner.Kind == IncludePatchOwnerKind.Patch)
                {
                    if (!patchChanges.TryGetValue(keyOwner.Index, out var changes))
                    {
                        changes = [];
                        patchChanges[keyOwner.Index] = changes;
                    }
                    changes[key] = value;
                }
                else
                {
                    fileChanges[key] = value;
                }
            }

            if (fileChanges.Count > 0)
            {
                IncludeJournal.ApplyChanges(flat.Options, fileChanges);
                fileChanged = true;
            }
            foreach (var (patchIndex, changes) in patchChanges)
            {
                ApplyChanges(patches[patchIndex], changes);
                patched = true;
            }

            if (record.Relocated && flat.Parent != record.Parent)
            {
                IncludeJournal.Detach(data, id);
                if (IncludeJournal.Place(data, flat.Options, record.Parent, record.Position)) fileChanged = true;
            }
        }

        return new IncludeRouteResult(fileChanged, patched);
    }

    public static void ApplyChanges(object target, Dictionary<string, object?> changes)
    {
        if (target is IDictionary<string, object?> dict)
        {
            foreach (var (key, value) in changes)
            {
                if (value is null) dict.Remove(key);
                else dict[key] = value;
            }
            return;
        }
        if (target is EntryOptions options)
        {
            IncludeJournal.ApplyChanges(options, changes);
        }
    }
}
